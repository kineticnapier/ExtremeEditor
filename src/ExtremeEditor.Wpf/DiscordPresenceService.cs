using System.Diagnostics;
using DiscordRPC;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf;

internal sealed class DiscordPresenceService : IDisposable
{
    private const string ApplicationId = "1558905252282114178";

    private DiscordRpcClient? _client;

    internal void Initialize()
    {
        if (_client is not null)
            return;

        DiscordRpcClient? client = null;
        try
        {
            client = new DiscordRpcClient(ApplicationId);
            if (!client.Initialize())
            {
                client.Dispose();
                return;
            }

            _client = client;
            SetIdle();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[discord-rp] initialization failed: {ex.Message}");
            client?.Dispose();
        }
    }

    internal void SetIdle()
    {
        SetPresence(
            details: "In ExtremeEditor",
            state: EditorVersion.Current);
    }

    internal void SetEditing(int floorCount)
    {
        SetPresence(
            details: "Editing a level",
            state: $"{floorCount:N0} floors");
    }

    private void SetPresence(string details, string state)
    {
        DiscordRpcClient? client = _client;
        if (client is null)
            return;

        try
        {
            client.SetPresence(new RichPresence
            {
                Details = details,
                State = state
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[discord-rp] presence update failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        DiscordRpcClient? client = _client;
        _client = null;
        if (client is null)
            return;

        try
        {
            client.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[discord-rp] shutdown failed: {ex.Message}");
        }
    }
}
