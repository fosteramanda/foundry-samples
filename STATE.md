# STATE

## Current
_Updated 2026-10-14 from the full log. History: STATE-HISTORY.md in this folder._

**What it is:** Amanda's fork of the Foundry sample catalogue, used to build C# autopilot samples and investigate agent APIs. The historical `-ado` folder name does not indicate Azure DevOps hosting.

**Decisions in force:**
- Use `autopilot-toolbox` on `https://github.com/fosteramanda/foundry-samples.git`. Push only to the fork; upstream pull requests need explicit approval. The `agentic-colleague` sample deletions were deliberate, not a sparse-checkout error.
- The latest router work is in `samples\csharp\foundry-autopilot-router-agent`, deploying `autopilotrouter` in NotARealCo (`dfa98250-28be-4cda-b270-45c05319c07c`). Version 14 is active at 100%; immediate rollback is v13, pre-card rollback v12. Telemetry confirms the Agentic Colleague instance uses this deployment.
- Planner replaces the private work-item tracker when configured. `IPST Board` belongs to its dedicated group. Confirm shared-record writes, retain evidence in task notes, and refuse ambiguous owners. A meeting invitation constitutes consent; explicit exclusions remain respected.
- Keep deployments distinct: original `workstreammanagerado` is v30 with `BotServiceRbac` and toolbox `workstream-manager-ado` v7; Office of Amanda, `autopilotroutera2a`, remains v40; the parked A2A workstream-manager fork, `workstreammanagera2arouter`, remains v3 with both `Entra` and `BotServiceTenant`.
- Office of Amanda's A2A arm uses dynamic discovery and invocation, not hardcoded agent connections or a Work IQ MCP fallback.
- Invocation message content defaults off. Application spans are in `dependencies`, named `invoke_agent {name}`, distinct from platform spans. Verify the responding session's version, not merely the traffic selector.

**Open items:**
- Router v13 delivered a scheduled Adaptive Card successfully. V14 adds the malformed-receipt guard; all 45 tests pass, but its actual delivery and the received card's appearance remain unchecked.
- Planner owner resolution and “me” still need agent-token runtime checks. `GroupMember.Read.All` is absent, disabling access warnings; the original Stripe card still records its owner only in notes.
- Office of Amanda's tracing passes 17 tests but is undeployed, alongside earlier fixes. The original Workstream Manager's conversation-state fix is also undeployed. A2A follow-ups remain unverified.
- Payments' five-scope consent repair works at runtime. Real Teams delivery after adding Entra remains unverified; synthetic calls return 500. Provisioning scripts can overwrite the repaired authorization and omit required permission declarations.
- Router routine listing returns empty despite scheduled executions. The isolated Azure CLI profile's broken extension blocks extension commands.
- AgentX registration probing remains blocked; NotARealCo has no E5 for the requested license test.

**Next step:** Verify a real router turn uses v14 and inspect its delivered card, then run the existing solo and group-chat test guides. Keep Payments and Office work separate.

Keep this file under 500 words: update Current in place, and put dated detail and history in
STATE-HISTORY.md in this folder. Decisions waiting on Amanda go in C:\src\office-of-amanda\WAITING.md.
