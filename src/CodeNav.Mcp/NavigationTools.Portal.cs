using System.ComponentModel;
using ModelContextProtocol.Server;

namespace CodeNav.Mcp;

public sealed partial class NavigationTools
{
    [McpServerTool(Name = "open_operations_portal")]
    [Description("Start or reuse the local read-only Phoenix Operations Portal for this workspace. Call this only when the user explicitly asks to open or show the portal. The tool does not open a browser: after success, present the returned url field verbatim to the user as a clickable link.")]
    public async Task<string> OpenOperationsPortal(CancellationToken cancellationToken = default)
    {
        OperationsPortalLaunchResult result = await _operationsPortalLauncher.LaunchAsync(
            _manager.WorkspaceRoot,
            cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return Json.Serialize(new
            {
                error = result.Error,
                detail = result.Detail,
                retryable = result.Retryable,
                meta = Meta.From(_manager.Health(), "indexed", "text"),
            });
        }

        return Json.Serialize(new
        {
            ready = true,
            status = result.Status,
            message = "Phoenix Operations Portal is ready.",
            url = result.Url,
            pid = result.Pid,
            workspaceCount = result.WorkspaceCount,
            readOnly = true,
            browserOpened = false,
            instruction = "Show the url field verbatim to the user as a clickable link.",
            meta = Meta.From(_manager.Health(), "indexed", "text"),
        });
    }
}
