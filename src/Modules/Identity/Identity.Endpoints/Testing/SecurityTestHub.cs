using Identity.Endpoints.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Identity.Endpoints.Testing;

[Authorize(Policy = IdentityPolicies.ActiveIdentity)]
public sealed class SecurityTestHub : Hub;
