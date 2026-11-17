using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace MatchZy;

// REIGN extras: HUD for dead players while a match is live, and rotating chat advertisements.
public partial class MatchZy
{
    public bool deadHudEnabled = true;
    public int adIntervalSeconds = 300;
    private int deadHudTick = 0;
    private int adIndex = 0;
    private CounterStrikeSharp.API.Modules.Timers.Timer? adTimer;

    private static readonly string[] DefaultAds =
    [
        "{Green}Discord-д нэгдээрэй: {Default}https://discord.gg/G525Nh2sCh",
        "{Green}REIGN {Default}серверийн хөгжүүлэгч - {Green}iammark_c",
        "{Default}Бэлэн болохдоо {Green}E{Default} дарах эсвэл {Green}.r{Default} гэж бичээрэй.",
    ];

    [ConsoleCommand("matchzy_dead_hud", "Show a HUD message to dead players during a live match. 1 = on, 0 = off. Default: 1")]
    public void DeadHudCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player != null) return;
        string arg = GetSettingArgument(command);
        if (arg == "0" || arg == "1") deadHudEnabled = arg == "1";
    }

    [ConsoleCommand("matchzy_ad_interval", "Seconds between chat advertisements (messages in cfg/MatchZy/advertisements.txt, one per line, {Green} style colors). 0 = off. Default: 300")]
    public void AdIntervalCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player != null) return;
        if (int.TryParse(GetSettingArgument(command), out int seconds) && seconds >= 0)
        {
            adIntervalSeconds = seconds;
            RestartAdTimer();
        }
    }

    private void RegisterServerExtras()
    {
        RegisterListener<Listeners.OnTick>(OnDeadHudTick);
        RestartAdTimer();
    }

    private void RestartAdTimer()
    {
        adTimer?.Kill();
        adTimer = null;
        if (adIntervalSeconds <= 0) return;
        adTimer = AddTimer(adIntervalSeconds, SendAdvertisement, TimerFlags.REPEAT);
    }

    private string[] LoadAds()
    {
        try
        {
            string path = Path.Combine(Server.GameDirectory, "csgo", "cfg", "MatchZy", "advertisements.txt");
            if (File.Exists(path))
            {
                var lines = File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("//") && !l.StartsWith("#")).ToArray();
                if (lines.Length > 0) return lines;
            }
        }
        catch (Exception e)
        {
            Log($"[LoadAds] {e.Message}");
        }
        return DefaultAds;
    }

    private void SendAdvertisement()
    {
        if (Utilities.GetPlayers().All(p => p == null || !p.IsValid || p.IsBot)) return;
        string[] ads = LoadAds();
        string ad = ads[adIndex++ % ads.Length];
        PrintToAllChat(GetColorTreatedString(ad));
    }

    private void OnDeadHudTick()
    {
        if (!deadHudEnabled || !isMatchLive || isPractice) return;
        if (++deadHudTick % 16 != 0) return;

        foreach (var player in Utilities.GetPlayers())
        {
            if (player == null || !player.IsValid || player.IsBot || player.IsHLTV) continue;
            if (player.TeamNum != (int)CsTeam.Terrorist && player.TeamNum != (int)CsTeam.CounterTerrorist) continue;
            if (player.PawnIsAlive) continue;

            player.PrintToCenterHtml(
                "<font color='#ffd34d' class='fontSize-l'>Та яг одоо <b>REIGN</b> сервер дээр тоглож байна</font><br>" +
                "<font color='#cccccc' class='fontSize-s'>discord.gg/G525Nh2sCh</font>", 1);
        }
    }
}
