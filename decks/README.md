# Decks

## Agents as Tools, Agents as Teammates

`Agents-as-Tools-Agents-as-Teammates.pptx` — five slides intended to be inserted into
*The Future of Human-Agent Interaction*, replacing the abstract "agents as tools" slide with a
concrete progression:

1. **Where we are now** — Foundry model router: the platform picks the model.
2. **Where we are now** — Foundry agent + toolbox: the agent picks the tool.
3. **Where we are going next** — Agent router: an orchestrator picks the agent, every hop
   on-behalf-of the user.
4. **Where we are going after that** — Autopilot router: proactive, across surfaces, delegating a
   job-to-be-done.
5. **What about what we already built** — Autopilot delegating into the M365 Agent Store /
   "My agents" catalog of thousands of existing agents.

The deck is generated so it can be regenerated or restyled deterministically:

```bash
pip install python-pptx
python decks/build_agent_routing_deck.py decks/Agents-as-Tools-Agents-as-Teammates.pptx
```

Style: 16:9, Segoe UI, white canvas, blue accent (#0F6CBD) for platform/routing, purple (#8A64D6)
for agents, teal (#0E7C66) for tools, amber (#B86E00) for identity and governance callouts. Each
slide uses eyebrow → title → accent rule → subtitle → diagram → takeaway bar.
