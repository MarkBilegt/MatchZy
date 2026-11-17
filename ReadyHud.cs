using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Commands;

namespace MatchZy;

// "Press E to ready" + a center-screen HUD hint while the match is waiting for players to ready up.
// Chat commands (.r .ready !r !ready) are handled elsewhere; this is the keybind/HUD path on top of them.
public partial class MatchZy
{
    public bool readyHudEnabled = true;

    [ConsoleCommand("matchzy_ready_hud", "Show a ready-up HUD and let players ready with the E (use) key. 1 = on, 0 = off. Default: 1")]
    public void ReadyHudCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player != null) return;
        string arg = GetSettingArgument(command);
        if (arg == "0" || arg == "1") readyHudEnabled = arg == "1";
    }

    private readonly Dictionary<int, bool> readyUseKeyHeld = new();
    private int readyHudTick = 0;

    private void RegisterReadyHud()
    {
        RegisterListener<Listeners.OnTick>(OnReadyHudTick);
    }

    private void OnReadyHudTick()
    {
        if (!readyHudEnabled) return;
        if (!readyAvailable || matchStarted || isPractice || IsJoinReadyMode())
        {
            if (readyUseKeyHeld.Count > 0) readyUseKeyHeld.Clear();
            return;
        }

        bool drawHud = ++readyHudTick % 16 == 0;

        foreach (var player in Utilities.GetPlayers())
        {
            if (player == null || !player.IsValid || player.IsBot || player.IsHLTV) continue;
            if (player.TeamNum < (int)CsTeam.Spectator || !player.UserId.HasValue) continue;
            int userId = player.UserId.Value;

            bool useHeld = (player.Buttons & PlayerButtons.Use) != 0;
            readyUseKeyHeld.TryGetValue(userId, out bool wasHeld);
            readyUseKeyHeld[userId] = useHeld;

            playerReadyStatus.TryGetValue(userId, out bool isReady);

            // Rising edge only, so holding E does not spam, and E never un-readies.
            if (useHeld && !wasHeld && !isReady)
            {
                OnPlayerReady(player, null);
                isReady = true;
            }

            if (drawHud)
            {
                string html = isReady
                    ? "<font color='#55ff55' class='fontSize-l'>You are READY</font><br><font color='#cccccc' class='fontSize-s'>Waiting for other players... (.unready to cancel)</font>"
                    : "<font color='#ffd34d' class='fontSize-l'>Press <b>E</b> to READY</font><br><font color='#cccccc' class='fontSize-s'>or type .r  .ready  !r  !ready</font>";
                player.PrintToCenterHtml(html, 1);
            }
        }
    }
}
