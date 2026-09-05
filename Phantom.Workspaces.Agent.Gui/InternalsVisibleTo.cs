using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Phantom.Workspaces.Agent.Gui.Tests")]
[assembly: InternalsVisibleTo("Phantom.Workspaces.Agent.Gui.WebViewTests")]
// #1451: the resume-notification suppression tests live in Phantom.Workspaces.Tests and drive the
// deterministic restore window via the internal SetHistoryPopulatedForTest seam.
[assembly: InternalsVisibleTo("Phantom.Workspaces.Tests")]
