<!--
---
name: Azure Function using Azure AI agent service to process a action function call using queues
description: Azure Function using Azure AI agent service to process a action function call using queues
page_type: sample
products:
- azure-functions
- azure
- entra-id
- azure-openai
urlFragment: azure-functions-ai-services-agent-dotnet
languages:
- C#
- bicep
- azdeveloper
---
-->

# Azure Functions
## Using Azure Functions to enable function calling from Azure AI Agent service

This sample highlights how to use the [Azure AI Agent service](https://learn.microsoft.com/en-us/azure/ai-studio/how-to/develop/sdk-overview?tabs=sync&pivots=programming-language-csharp#azure-ai-agent-service) function calling where function calls are placed on a storage queue by the Agent service to be processed by an Azure Function listening to that queue. 

You can learn more about Azure functions in the [Official documentation](https://learn.microsoft.com/en-us/azure/azure-functions)

The [`app`](./app/) folder contains the function code used in this sample while the infra folder contains all of the Azure resources that need to be created.

## Prerequisites

* [Azure Functions Core Tools v4.x](https://learn.microsoft.com/azure/azure-functions/functions-run-local?tabs=v4%2Cwindows%2Cnode%2Cportal%2Cbash)
* [Azure AI Agent Service](https://learn.microsoft.com/en-us/azure/ai-studio/how-to/develop/sdk-overview?tabs=sync&pivots=programming-language-csharp#azure-ai-agent-service)
* [Azurite](https://github.com/Azure/Azurite)

## Prepare your local environment

### Create Azure resources for local and cloud dev-test

Once you have your Azure subscription, run the following in a new terminal window to create Azure OpenAI and other resources needed:

```bash
azd init --template https://github.com/Azure-Samples/azure-functions-ai-services-agent-dotnet
```
Mac/Linux:
```bash
chmod +x ./infra/scripts/*.sh 
```
Windows:
```Powershell
set-executionpolicy remotesigned
```
Run the follow command to provision resources in Azure
```bash
azd provision
```

### Create local.settings.json (Should be in the same folder as host.json. Automatically created if you ran azd provision)
```json
{
  "IsEncrypted": false,
  "Values": {
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "STORAGE_CONNECTION__queueServiceUri": "https://<storageaccount>.queue.core.windows.net",
    "PROJECT_ENDPOINT": "<project endpoint for AI Project>",
    "MODEL_DEPLOYMENT_NAME": "<model deployment name>",
    "AzureWebJobsStorage": "UseDevelopmentStorage=true"
    }
}
```

### Authentication

This sample uses **managed identity authentication** for secure, production-ready access to Azure resources:

- **Local Development**: Uses `DefaultAzureCredential` which automatically detects your Azure CLI login or Visual Studio credentials
- **Azure Deployment**: Uses a user-assigned managed identity that is automatically configured by the infrastructure

The authentication is handled transparently - the code automatically detects whether it's running locally or in Azure and uses the appropriate authentication method.

## Run your app using Visual Studio Code

1. Open the folder in a new terminal.
1. Run the `code .` code command to open the project in Visual Studio Code.
1. In the command palette (F1), type `Azurite: Start`, which enables debugging with local storage for Azure Functions runtime.
1. Press **Run/Debug (F5)** to run in the debugger. Select **Debug anyway** if prompted about local emulator not running.
1. Send POST `prompt` endpoints respectively using your HTTP test tool. If you have the [RestClient](https://marketplace.visualstudio.com/items?itemName=humao.rest-client) extension installed, you can execute requests directly from the [`test.http`](./app/test.http) project file.


## Deploy to Azure

Run this command to provision the function app, with any required Azure resources, and deploy your code:

```shell
azd up
```

You're prompted to supply these required deployment parameters:

| Parameter | Description |
| ---- | ---- |
| _Environment name_ | An environment that's used to maintain a unique deployment context for your app. You won't be prompted if you created the local project using `azd init`.|
| _Azure subscription_ | Subscription in which your resources are created.|
| _Azure location_ | Azure region in which to create the resource group that contains the new Azure resources. Only regions that currently support the Flex Consumption plan are shown.|

After publish completes successfully, `azd` provides you with the URL endpoints of your new functions, but without the function key values required to access the endpoints. To learn how to obtain these same endpoints along with the required function keys, see [Invoke the function on Azure](https://learn.microsoft.com/azure/azure-functions/create-first-function-azure-developer-cli?pivots=programming-language-dotnet#invoke-the-function-on-azure) in the companion article [Quickstart: Create and deploy functions to Azure Functions using the Azure Developer CLI](https://learn.microsoft.com/azure/azure-functions/create-first-function-azure-developer-cli?pivots=programming-language-dotnet).

## Redeploy your code

You can run the `azd up` command as many times as you need to both provision your Azure resources and deploy code updates to your function app.

>[!NOTE]
>Deployed code files are always overwritten by the latest deployment package.

## Clean up resources

When you're done working with your function app and related resources, you can use this command to delete the function app and its related resources from Azure and avoid incurring any further costs (--purge does not leave a soft delete of AI resource and recovers your quota):

```shell
azd down --purge
```
