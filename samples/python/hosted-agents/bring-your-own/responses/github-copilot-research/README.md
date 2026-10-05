# Research test agent on the Responses protocol (test build)

This is the same GitHub Copilot SDK research agent as the Activity test build in
`../../activity/github-copilot` (agent `amandaf-lr-icons`), with one difference: its
code speaks only the **Responses** protocol. It has no Teams or Microsoft 365 code.
It exists to compare what users see in Teams and Microsoft 365 Copilot when an agent
relies on Foundry's protocol translation instead of handling the Activity protocol
itself.

## How it works

- `main.py` hosts the agent with `azure-ai-agentserver-responses`. Each request is one
  chat turn. Shared files and images in the request input (`input_file`,
  `input_image`) are saved under `$HOME` and handed to the model to read.
- `client.py` drives the GitHub Copilot SDK (model plus tool loop), the same harness
  as the Activity build, including its turn watchdog: a turn with no progress for 5
  minutes, a single model call running over 4 minutes, or a turn over 25 minutes is
  stopped, the Copilot runtime is restarted, and the turn is retried once.
- `tools.py` has the same tools minus anything that needs a channel UI: a to-do list
  (returned as text, not an Adaptive Card) and `search_web` (Foundry's built-in web
  search). There is no file delivery tool, because the Responses protocol has no way
  to send a file; reports go in the reply as Markdown.
- The reply streams as an output message. Progress ("Searching the web...") streams
  as reasoning summary items, the Responses protocol's place for what the agent is
  doing, so it never ends up in the reply text.

## How Teams and Microsoft 365 Copilot reach it

The agent is deployed with only the `responses` protocol. Publishing adds the rest:

1. Create an Azure Bot Service resource whose endpoint is the agent's Activity
   Protocol route and whose app ID is the agent's identity client ID.
2. Call Foundry's Microsoft 365 publish API, or its package download API
   (`/agents/<name>/microsoft365/zip`). Preparing the package turns on the `activity`
   protocol and a Bot Service authorization scheme on the agent endpoint.

From then on, Foundry translates: a Teams message becomes a Responses request to this
code, and the response stream becomes Teams messages. Steps:
[Publish agents to Microsoft Copilot and Microsoft Teams by using the REST API](https://learn.microsoft.com/azure/foundry/agents/how-to/publish-copilot-virtual-network).

What the translation shows today (Foundry code, October 2026): reply text (streamed
live only where Foundry's streaming flag is on), its own "Please wait while I process
your request" and typing indicator instead of this agent's progress, URL citations,
and images from built-in tools. It has no file download card.

## Deploy

```bash
azd deploy amandaf-lr-responses
```

The project needs a `gpt-5-mini` deployment for the chat model and a `gpt-4o`
deployment for web search. The Copilot SDK only works with reasoning models here:
it sends a reasoning setting and a custom tool type that `gpt-4o` rejects.

## Run locally

```bash
pip install -r src/github-copilot-research/requirements.txt
cd src/github-copilot-research
python main.py   # listens on http://localhost:8088/responses
```

Set the variables in `.env.example`. Set `COPILOT_HOME` to an empty folder and run
from a folder outside any repository: otherwise the Copilot runtime loads your own
Copilot setup (MCP servers, custom instructions) and local results won't match the
hosted agent.

## Known issue

With the default content filter (`Microsoft.DefaultV2`), research about Kyoto
(for example "Research and plan a 5-day trip to Kyoto in April with costs.") is often
blocked: the filter rates plain questions about Kyoto attractions such as
Kiyomizu-dera, Fushimi Inari, and tea ceremonies as sexual content (medium to high
severity). When the model's output is blocked, the Copilot runtime replies "I'm sorry,
but I cannot assist with that request." and the agent logs a warning; a blocked tool
call can also stream junk input until the 4-minute model-call limit stops it. Use
another topic to test; "Research and plan a 5-day trip to Lisbon in April with
costs." ran for about 7 minutes and returned a cited report.