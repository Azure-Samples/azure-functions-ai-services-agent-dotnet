using System.Text.Json;
using Azure;
using Azure.AI.Projects;
using Azure.AI.Agents.Persistent;
using Azure.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

public static class FunctionApp
{
    private const string inputQueueName = "input";
    private const string outputQueueName = "output";

    private static (AIProjectClient, PersistentAgentThread, PersistentAgent) InitializeClient(ILogger logger)
    {
        try
        {
            // Create a project client using the project endpoint from local.settings.json
            var projectEndpoint = Environment.GetEnvironmentVariable("PROJECT_ENDPOINT");
            if (string.IsNullOrEmpty(projectEndpoint))
            {
                throw new InvalidOperationException("PROJECT_ENDPOINT is not set.");
            }

            // Get the managed identity client ID for user-assigned managed identity
            var clientId = Environment.GetEnvironmentVariable("PROJECT_ENDPOINT__clientId");
            
            // Create credential - use specific managed identity client ID if available (for Azure deployment)
            // or fallback to DefaultAzureCredential for local development
            Azure.Core.TokenCredential credential;
            if (!string.IsNullOrEmpty(clientId))
            {
                credential = new ManagedIdentityCredential(clientId);
                logger.LogInformation($"Using user-assigned managed identity with client ID: {clientId}");
            }
            else
            {
                credential = new DefaultAzureCredential();
                logger.LogInformation("Using DefaultAzureCredential for local development");
            }

            AIProjectClient projectClient = new AIProjectClient(new Uri(projectEndpoint), credential);
            PersistentAgentsClient agentsClient = projectClient.GetPersistentAgentsClient();

            // Get the connection string from local.settings.json
            var storageConnectionString = Environment.GetEnvironmentVariable("STORAGE_CONNECTION__queueServiceUri");
            if (string.IsNullOrEmpty(storageConnectionString))
            {
                throw new InvalidOperationException("STORAGE_CONNECTION__queueServiceUri is not set.");
            }

            // Tool definition for the Azure Function
            AzureFunctionToolDefinition azureFunctionTool = new(
                name: "GetWeather",
                description: "Get the weather in a location.",
                inputBinding: new AzureFunctionBinding(
                    new AzureFunctionStorageQueue(
                        queueName: inputQueueName,
                        storageServiceEndpoint: storageConnectionString
                    )
                ),
                outputBinding: new AzureFunctionBinding(
                    new AzureFunctionStorageQueue(
                        queueName: outputQueueName,
                        storageServiceEndpoint: storageConnectionString
                    )
                ),
                parameters: BinaryData.FromObjectAsJson(
                        new
                        {
                            Type = "object",
                            Properties = new
                            {
                                location = new
                                {
                                    Type = "string",
                                    Description = "The location to look up.",
                                }
                            },
                        },
                    new JsonSerializerOptions() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }
                )
            );

            // Get model name from environment or use default
            var modelName = Environment.GetEnvironmentVariable("MODEL_DEPLOYMENT_NAME") ?? "gpt-4.1-mini";

            // Create an agent with the Azure Function tool to get the weather
            PersistentAgent agent = agentsClient.Administration.CreateAgent(
                model: modelName,
                name: "azure-function-agent-get-weather",
                instructions: "You are a helpful support agent. Answer the user's questions to the best of your ability.",
                tools: new List<ToolDefinition> { azureFunctionTool }
            );

            logger.LogInformation($"Created agent, agent ID: {agent.Id}");

            // Create a thread
            PersistentAgentThread thread = agentsClient.Threads.CreateThread();
            logger.LogInformation($"Created thread, thread ID: {thread.Id}");

            return (projectClient, thread, agent);
        }
        catch (Exception ex)
        {
            logger.LogError($"Error initializing client: {ex.Message}");
            throw;
        }
    }

    [Function("Prompt")]
    public static async Task<IActionResult> Prompt([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData req, FunctionContext executionContext)
    {
        var logger = executionContext.GetLogger("Prompt");
        logger.LogInformation("C# HTTP trigger function processed a request.");

        try
        {
            // Get the prompt from the request body
            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(requestBody);
            if (data == null || !data.TryGetValue("Prompt", out var prompt))
            {
                return new BadRequestObjectResult("The 'Prompt' field is missing in the request body.");
            }

            // Initialize the agent client and thread
            var (projectClient, thread, agent) = InitializeClient(logger);
            var agentsClient = projectClient.GetPersistentAgentsClient();

            // Send the prompt to the agent
            PersistentThreadMessage messageResponse = agentsClient.Messages.CreateMessage(
                thread.Id,
                MessageRole.User,
                prompt);

            ThreadRun runResponse = agentsClient.Runs.CreateRun(thread.Id, agent.Id);

            // Poll the run until it's completed
            do
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500));
                runResponse = agentsClient.Runs.GetRun(thread.Id, runResponse.Id);
            }
            while (runResponse.Status == RunStatus.Queued
                || runResponse.Status == RunStatus.InProgress
                || runResponse.Status == RunStatus.RequiresAction);

            // Get messages from the assistant thread
            var messages = agentsClient.Messages.GetMessages(thread.Id);
            logger.LogInformation($"Messages: {messages}");

            // Get the most recent message from the assistant
            string lastMsg = string.Empty;
            foreach (PersistentThreadMessage threadMessage in messages)
            {
                MessageContent contentItem = threadMessage.ContentItems[0];
                if (contentItem is MessageTextContent textItem)
                {
                    lastMsg = textItem.Text;
                    break;
                }
            }

            logger.LogInformation($"Most recent message: {lastMsg}");

            // Delete the agent once done
            agentsClient.Administration.DeleteAgent(agent.Id);
            logger.LogInformation("Deleted agent");

            return new OkObjectResult(lastMsg);
        }
        catch (Exception ex)
        {
            logger.LogError($"Error processing prompt: {ex.Message}");
            return new BadRequestObjectResult("An error occurred while processing the request.");
        }
    }

    [Function("GetWeather")]
    [QueueOutput(outputQueueName, Connection = "STORAGE_CONNECTION")]
    public static string ProcessQueueMessage([QueueTrigger(inputQueueName, Connection = "STORAGE_CONNECTION")] string msg, FunctionContext context)
    {
        var logger = context.GetLogger("GetWeather");
        logger.LogInformation("C# queue trigger function processed a queue item");

        try
        {
            // Deserialize the message payload and get the location and correlation ID
            var messagePayload = JsonSerializer.Deserialize<Dictionary<string, string>>(msg);
            if (messagePayload == null || !messagePayload.TryGetValue("location", out var location))
            {
                throw new ArgumentNullException("The 'location' field is missing in the message payload.");
            }
            var correlationId = messagePayload["CorrelationId"];

            // Send message to queue. Sends a mock message for the weather
            var resultMessage = new Dictionary<string, string>
            {
                { "Value", $"Weather is 74 degrees and sunny in {location}" },
                { "CorrelationId", correlationId }
            };

            logger.LogInformation($"Sent message to queue: {outputQueueName}");
            return JsonSerializer.Serialize(resultMessage);
        }
        catch (Exception ex)
        {
            logger.LogError($"Error processing queue message: {ex.Message}");
            throw;
        }
    }
}