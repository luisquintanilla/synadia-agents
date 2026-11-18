# .NET SDK diagnostics

Diagnostic IDs reported by the Synadia .NET packages. Experimental APIs may change or be removed in any release,
including patch releases.

## SYNAGENT001

`AIAgentServeOptions.SessionStore` (package `Synadia.AI.Agents.AgentFramework`) is experimental.

It builds on Agent Framework's `AgentSessionStore`, which is itself experimental (`MAAI001`). Its shape will follow
that API until both are stable.

To use it, suppress both diagnostics where you set the property:

```csharp
#pragma warning disable SYNAGENT001, MAAI001
var options = new AIAgentServeOptions { SessionStore = store };
#pragma warning restore SYNAGENT001, MAAI001
```

Or for a whole project:

```xml
<PropertyGroup>
  <NoWarn>$(NoWarn);SYNAGENT001;MAAI001</NoWarn>
</PropertyGroup>
```
