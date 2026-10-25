// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.AspNetCore.SignalR;

namespace Tonregal.Web.Hubs;

/// <summary>Live-Status für die SPA: Import-Fortschritt, Analyse-Queue, Backup.</summary>
public sealed class StatusHub : Hub;
