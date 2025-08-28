# Azure AI Agents function calling with Azure Functions

Azure AI Agents supports function calling, which allows you to describe the structure of functions to an Assistant and then return the functions that need to be called along with their arguments. This example shows how to use Azure Functions to process the function calls through queue messages in Azure Storage. You can see a complete working sample on https://github.com/Azure-Samples/azure-functions-ai-services-agent-dotnet

### Supported models

To use all features of function calling including parallel functions, you need to use a model that was released after November 6, 2023.

## Required Using Statements

```C#
using System.Text.Json;
using Azure;
using Azure.AI.Projects;
using Azure.AI.Agents.Persistent;
using Azure.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
```

## Define a function for your agent to call

Start by defining an Azure Function queue trigger function that will process function calls from the queue. 

```C#
// Function to get the weather from an Azure Storage queue where the AI Agent will send function call information
// It returns the mock weather to an output queue with the correlation id for the AI Agent service to pick up the result of the function call
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
```

---

## Create an AI project client and agent

In the sample below we create a client and an agent that has the tools definition for the Azure Function

```C#
// Initialize the client and create agent for the tools Azure Functions that the agent can use
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
```

---

## Create a run and check the output

```C#
// Initialize the agent client and thread
var (projectClient, thread, agent) = InitializeClient(logger);
var agentsClient = projectClient.GetPersistentAgentsClient();

// Send the prompt to the agent
PersistentThreadMessage messageResponse = agentsClient.Messages.CreateMessage(
    thread.Id,
    MessageRole.User,
    "What is the weather in Seattle?");

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
```

---

### Get the result of the run and print out

```C#
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
```

## Configuration

### Authentication

This sample uses **managed identity authentication** for secure access to Azure AI Project resources:

- **Local Development**: Uses `DefaultAzureCredential` which will use your Azure CLI login or Visual Studio credentials
- **Azure Deployment**: Uses a user-assigned managed identity with the client ID specified in `PROJECT_ENDPOINT__clientId`

The authentication logic automatically detects the environment and uses the appropriate credential type.

### Required Environment Variables

The following environment variables must be configured in your `local.settings.json` file for local development:

```json
{
  "IsEncrypted": "false",
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "PROJECT_ENDPOINT": "<project endpoint for AI Project>",
    "MODEL_DEPLOYMENT_NAME": "<model deployment name>",
    "STORAGE_CONNECTION__queueServiceUri": "<queue service URI for Azure Storage>"
  }
}
```

- `PROJECT_ENDPOINT`: The endpoint URL for your Azure AI Project
- `MODEL_DEPLOYMENT_NAME`: The name of the deployed AI model (e.g., "gpt-4o-mini")
- `STORAGE_CONNECTION__queueServiceUri`: Queue service URI for Azure Storage
- `AzureWebJobsStorage`: Storage connection for Azure Functions runtime

**Note**: When deployed to Azure, the infrastructure automatically provides additional environment variables like `PROJECT_ENDPOINT__clientId` for managed identity authentication.
