FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

ARG BLUEPRINT_CLIENT_ID
ARG AUTHORITY_ENDPOINT
ARG AZURE_OPENAI_ENDPOINT
ARG MODEL_DEPLOYMENT
ARG PROJECT_DEFAULT_INSTANCE_CLIENT_ID
ARG WORK_ITEMS_TABLE_SERVICE_URI
ARG WORK_ITEMS_TABLE_NAME

# Foundry trace-based evaluations. FOUNDRY_AGENT_NAME and FOUNDRY_AGENT_VERSION are injected by
# the hosted runtime, so they are deliberately NOT declared here. The project ARM id is not, and
# without it the invoke_agent spans carry no microsoft.foundry.project.id and evaluations cannot
# attribute the run to a project.
ARG FOUNDRY_PROJECT_ARM_ID

# Off by default: this is the user's conversation text, and enabling it copies that text into
# whatever telemetry store the exporter points at. Turn it on deliberately, per environment.
ARG OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT=false
COPY ./publish .

# Expose the default port
EXPOSE 8088

ENV ASPNETCORE_URLS=http://+:8088

ENV MCP_PLATFORM_ENDPOINT=https://agent365.svc.cloud.microsoft

ENV Connections__ServiceConnection__Settings__ClientId=${BLUEPRINT_CLIENT_ID}
ENV Connections__ServiceConnection__Settings__AuthorityEndpoint=${AUTHORITY_ENDPOINT}
ENV AzureOpenAIEndpoint=${AZURE_OPENAI_ENDPOINT}
ENV ModelDeployment=${MODEL_DEPLOYMENT}
ENV ProjectDefaultInstanceClientId=${PROJECT_DEFAULT_INSTANCE_CLIENT_ID}
ENV WorkItemsTableServiceUri=${WORK_ITEMS_TABLE_SERVICE_URI}
ENV WorkItemsTableName=${WORK_ITEMS_TABLE_NAME}

ENV FOUNDRY_PROJECT_ARM_ID=${FOUNDRY_PROJECT_ARM_ID}
ENV OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT=${OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT}

# Start the application
ENTRYPOINT ["dotnet", "AutopilotRouterAgent.dll"]

