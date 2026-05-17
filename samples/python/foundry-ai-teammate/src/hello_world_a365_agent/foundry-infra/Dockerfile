FROM python:3.12-slim AS runtime
WORKDIR /app

ARG BLUEPRINT_CLIENT_ID
ARG AUTHORITY_ENDPOINT
ARG AZURE_OPENAI_ENDPOINT
ARG MODEL_DEPLOYMENT

# Install build essentials needed by some wheels (e.g. cryptography).
RUN apt-get update \
    && apt-get install -y --no-install-recommends build-essential curl \
    && rm -rf /var/lib/apt/lists/*

# Install Python dependencies first to maximize Docker layer caching.
COPY requirements.txt ./
RUN pip install --no-cache-dir --upgrade pip \
    && pip install --no-cache-dir --pre -r requirements.txt

# Copy application source.
COPY . .

# Expose the default port.
EXPOSE 8088

ENV PORT=8088
ENV MCP_PLATFORM_ENDPOINT=https://agent365.svc.cloud.microsoft

# Microsoft 365 Agents SDK reads these as configuration via the
# Connections__ServiceConnection__Settings__* environment-variable convention.
ENV Connections__ServiceConnection__Settings__ClientId=${BLUEPRINT_CLIENT_ID}
ENV Connections__ServiceConnection__Settings__AuthorityEndpoint=${AUTHORITY_ENDPOINT}
ENV AzureOpenAIEndpoint=${AZURE_OPENAI_ENDPOINT}
ENV ModelDeployment=${MODEL_DEPLOYMENT}

ENTRYPOINT ["python", "-m", "hello_world_a365_agent.app"]
