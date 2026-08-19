KadrStudio Stages 6-8 - conversation builder fix

Observed runtime error
----------------------
MoveToImmutable can only be performed when Count equals Capacity.

Root cause
----------
MainViewModel.BuildAgentConversationContext creates an ImmutableArray builder
with dynamic capacity and then calls MoveToImmutable(). MoveToImmutable() is
valid only when builder.Count == builder.Capacity.

This builder is intentionally dynamic:
- chat messages can be filtered;
- one question message can expand into an assistant message plus a user answer.

Fix
---
Use builder.ToImmutable() for this dynamic builder.

Only file changed:
  src/Kadr/ViewModels/MainViewModel.cs

Run
---
  Unblock-File -LiteralPath .\repair-agent-conversation-builder.ps1
  .\repair-agent-conversation-builder.ps1 -RepoRoot F:\KadrStudio\KadrStudio

The repair script:
- verifies the exact installed Stages 6-8 MainViewModel hash;
- backs up MainViewModel.cs;
- changes only the conversation builder implementation;
- updates the source timestamp;
- runs git diff --check;
- cleans affected Kadr/UI-test bin/obj;
- runs all UI adapter tests;
- builds KadrStudio Release with -warnaserror.
