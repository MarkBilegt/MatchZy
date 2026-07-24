using System.Runtime.InteropServices;
using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace MatchZy;

public partial class MatchZy
{
    private static readonly bool RaitoCosmeticsHandledByWeaponPaints = true;
    private const int RaitoInventoryServicesInventoryOffset = 112;
    private const int RaitoGloveLoadoutSlot = 41;
    private sealed record RaitoWeaponRefreshState(CBasePlayerWeapon Weapon, string DesignerName, int Clip1, int ReserveAmmo, bool RestoreAmmo);

    private readonly Dictionary<ulong, (string Role, DateTime ExpiresAt)> raitoRoleCache = new();
    private DateTime raitoRoleCacheInvalidatedAt = DateTime.MinValue;
    private readonly Dictionary<ulong, Dictionary<string, RaitoSkinRecord>> raitoSkinCache = new();
    private readonly Dictionary<string, string> raitoAdminCallStatuses = new();
    private readonly Dictionary<(ulong SteamId, int Team), nint> raitoGloveItemViews = new();
    private readonly object raitoSkinCacheLock = new();
    private long nextRaitoItemId = 65155030970;
    private long raitoGloveLoadoutHookHits;
    private RaitoDatabase? raitoDatabase;
    private RaitoServerConfig raitoConfig = new();
    private CounterStrikeSharp.API.Modules.Timers.Timer? raitoHeartbeatTimer;
    private CounterStrikeSharp.API.Modules.Timers.Timer? raitoCommandTimer;
    private int raitoCommandPollInProgress;
    private bool raitoSkinAttributesAllowed;
    private bool raitoSkinGuidelineWarningLogged;
    private MemoryFunctionVoid<nint, string, object>? raitoSetAttribute;
    private MemoryFunctionWithReturn<nint, int, int, nint>? raitoGetItemInLoadout;
    private MemoryFunctionVoid<nint>? raitoSendInventoryUpdate;
    private MemoryFunctionVoid<nint>? raitoSetWearables;
    private MemoryFunctionWithReturn<nint, nint>? raitoSetModelFromLoadout;
    private MemoryFunctionVoid<nint>? raitoSetModelFromClass;
    private MemoryFunctionWithReturn<nint, nint>? raitoEconItemViewConstructor;
    private MemoryFunctionWithReturn<nint, nint, nint>? raitoEconItemViewCopy;
    private bool raitoGloveLoadoutHookInstalled;
    private bool raitoSignatureWarningLogged;

    private void InitializeRaitoIntegration()
    {
        raitoConfig = RaitoServerConfig.Load();
        if (!RaitoCosmeticsHandledByWeaponPaints)
        {
            raitoSkinAttributesAllowed = LoadRaitoSkinAttributePermission();
            try
            {
                raitoSetAttribute = new MemoryFunctionVoid<nint, string, object>(GameData.GetSignature("CAttributeList_SetOrAddAttributeValueByName"));
            }
            catch (Exception ex)
            {
                Log($"[BETHECHAMP] Skin attribute signature is unavailable; model changes remain limited until gamedata is installed: {ex.Message}");
            }
            InitializeRaitoGloveLoadoutHook();
        }
        string databaseConfigPath = Path.Combine(Server.GameDirectory, "csgo", "cfg", "MatchZy", "database.json");

        try
        {
            if (File.Exists(databaseConfigPath))
            {
                raitoDatabase = new RaitoDatabase(databaseConfigPath);
                Log("[BETHECHAMP] Shared MySQL database connected.");
            }
            else
            {
                Log($"[BETHECHAMP] Shared database config not found at {databaseConfigPath}. Database-backed commands are disabled.");
            }
        }
        catch (Exception ex)
        {
            Log($"[BETHECHAMP] Shared database unavailable: {ex.Message}");
            raitoDatabase = null;
        }

        RegisterListener<Listeners.OnClientAuthorized>((playerSlot, steamId) => OnRaitoClientAuthorized(playerSlot, steamId.SteamId64));
        RegisterEventHandler<EventPlayerConnectFull>(OnRaitoPlayerConnectFull);
        if (!RaitoCosmeticsHandledByWeaponPaints)
        {
            RegisterEventHandler<EventPlayerSpawn>(OnRaitoPlayerSpawn);
            RegisterEventHandler<EventItemPickup>(OnRaitoItemPickup);
            VirtualFunctions.GiveNamedItemFunc.Hook(OnRaitoGiveNamedItemPost, HookMode.Post);
        }
        AddCommandListener("say", OnRaitoPlayerSay);
        AddCommandListener("say_team", OnRaitoPlayerSay);
        InitializeRaitoCommunityFeatures();
        InitializeRaitoRanking();
        PushRaitoHeartbeat();
        raitoHeartbeatTimer = AddTimer(raitoConfig.HeartbeatIntervalSeconds, PushRaitoHeartbeat, TimerFlags.REPEAT);
        PollRaitoGameCommands();
        raitoCommandTimer = AddTimer(1.0f, PollRaitoGameCommands, TimerFlags.REPEAT);
    }

    public override void Unload(bool hotReload)
    {
        ResetRaitoTestMatchState();
        DisposeRaitoRanking();
        if (!RaitoCosmeticsHandledByWeaponPaints)
        {
            VirtualFunctions.GiveNamedItemFunc.Unhook(OnRaitoGiveNamedItemPost, HookMode.Post);
        }
        if (raitoGloveLoadoutHookInstalled && raitoGetItemInLoadout is not null)
        {
            raitoGetItemInLoadout.Unhook(OnRaitoGetItemInLoadout, HookMode.Post);
            raitoGloveLoadoutHookInstalled = false;
        }
        ClearRaitoGloveItemViews();
        raitoHeartbeatTimer?.Kill();
        raitoHeartbeatTimer = null;
        raitoCommandTimer?.Kill();
        raitoCommandTimer = null;
        raitoDatabase?.Dispose();
        raitoDatabase = null;
        lock (raitoSkinCacheLock) raitoSkinCache.Clear();
    }

    private HookResult OnRaitoPlayerSay(CCSPlayerController? player, CommandInfo command)
    {
        if (!IsPlayerValid(player)) return HookResult.Continue;

        if (raitoDatabase is not null)
        {
            try
            {
                if (raitoDatabase.GetActiveModeration("Mute", player!.SteamID) is not null)
                {
                    PrintToPlayerChat(player, "Chat access is suspended for your account.");
                    return HookResult.Stop;
                }
            }
            catch (Exception ex)
            {
                Log($"[BETHECHAMP] Mute check failed: {ex.Message}");
                return HookResult.Continue;
            }
        }

        string rawMessage = NormalizeRaitoSayMessage(command.ArgString);
        if (TryHandleRaitoChatCommand(player, rawMessage))
        {
            return HookResult.Stop;
        }

        return HookResult.Continue;
    }

    private HookResult OnRaitoPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        CCSPlayerController? player = @event.Userid;
        if (!IsPlayerValid(player) || player!.IsBot || player.IsHLTV || raitoDatabase is null) return HookResult.Continue;

        try
        {
            if (raitoDatabase.GetActiveModeration("Gag", player!.SteamID) is not null)
            {
                player.VoiceFlags = VoiceFlags.Muted;
            }
        }
        catch (Exception ex)
        {
            Log($"[BETHECHAMP] Gag check failed: {ex.Message}");
        }

        return HookResult.Continue;
    }

    private HookResult OnRaitoPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        CCSPlayerController? player = @event.Userid;
        if (!IsPlayerValid(player) || raitoDatabase is null) return HookResult.Continue;

        LoadRaitoSkins(player!);
        AddTimer(0.25f, () => ApplyRaitoSkins(player!), TimerFlags.STOP_ON_MAPCHANGE);
        return HookResult.Continue;
    }

    private HookResult OnRaitoItemPickup(EventItemPickup @event, GameEventInfo info)
    {
        CCSPlayerController? player = @event.Userid;
        if (!IsPlayerValid(player) || raitoDatabase is null) return HookResult.Continue;

        AddTimer(0.1f, () => ApplyRaitoSkins(player!), TimerFlags.STOP_ON_MAPCHANGE);
        return HookResult.Continue;
    }

    internal bool RefreshRaitoSkins(CCSPlayerController? player)
    {
        if (!IsPlayerValid(player))
        {
            Log("[BETHECHAMP] Skin refresh rejected because the player is invalid.");
            return false;
        }

        if (raitoDatabase is null)
        {
            Log("[BETHECHAMP] Skin refresh rejected because the shared database is unavailable.");
            return false;
        }

        LoadRaitoSkins(player!);
        EnableRaitoSkinHud(player!);
        Dictionary<string, RaitoSkinRecord>? skins;
        lock (raitoSkinCacheLock) raitoSkinCache.TryGetValue(player!.SteamID, out skins);
        string side = player.TeamNum == (byte)CsTeam.CounterTerrorist ? "CT" : player.TeamNum == (byte)CsTeam.Terrorist ? "T" : "ALL";
        string loadedKeys = skins is null || skins.Count == 0 ? "none" : string.Join(", ", skins.Keys.OrderBy(key => key).Take(20));
        Log($"[BETHECHAMP] Skin refresh for {player.PlayerName} ({player.SteamID}): {skins?.Count ?? 0} saved slots, active side {side}. Keys: {loadedKeys}");
        ApplyRaitoSkins(player!, diagnostic: true, recreateWeapons: true);
        return true;
    }

    private void LoadRaitoSkins(CCSPlayerController player)
    {
        if (raitoDatabase is null) return;

        try
        {
            var skins = raitoDatabase.GetPlayerSkins(player.SteamID);
            lock (raitoSkinCacheLock) raitoSkinCache[player.SteamID] = skins;
        }
        catch (Exception ex)
        {
            Log($"[BETHECHAMP] Skin load failed for {player.SteamID}: {ex.Message}");
        }
    }

    private void ApplyRaitoSkins(CCSPlayerController player, bool diagnostic = false, bool recreateWeapons = false)
    {
        if (!raitoSkinAttributesAllowed)
        {
            if (!raitoSkinGuidelineWarningLogged)
            {
                raitoSkinGuidelineWarningLogged = true;
                Log("[BETHECHAMP] Skin attributes are disabled because CounterStrikeSharp FollowCS2ServerGuidelines is enabled. Set it to false in addons/counterstrikesharp/configs/core.json and restart the server.");
            }

            if (diagnostic) Log("[BETHECHAMP] Skin refresh stopped because skin attributes are disabled.");
            return;
        }

        if (!IsPlayerValid(player) || player.PlayerPawn.Value?.WeaponServices?.MyWeapons is not { } weapons)
        {
            if (diagnostic) Log("[BETHECHAMP] Skin refresh stopped because the player has no valid weapon services yet.");
            return;
        }

        Dictionary<string, RaitoSkinRecord>? skins;
        lock (raitoSkinCacheLock) raitoSkinCache.TryGetValue(player.SteamID, out skins);
        if (skins is null || skins.Count == 0)
        {
            if (diagnostic) Log("[BETHECHAMP] Skin refresh stopped because no saved skin slots were loaded.");
            return;
        }

        string side = player.TeamNum == (byte)CsTeam.CounterTerrorist ? "CT" : player.TeamNum == (byte)CsTeam.Terrorist ? "T" : "ALL";
        ApplyRaitoGloves(player, skins, side, diagnostic);
        int appliedWeapons = 0;
        List<string> matchedWeapons = new();
        List<RaitoWeaponRefreshState> weaponsToRecreate = new();

        foreach (var handle in weapons)
        {
            var weapon = handle.Value;
            if (weapon is null || !weapon.IsValid) continue;
            if (!ApplyRaitoSkinToWeapon(player, weapon, skins, side, out string weaponClass, out bool isKnife, out RaitoSkinRecord? skin)) continue;
            if (diagnostic) matchedWeapons.Add($"{weaponClass}={skin!.PaintKitId}");
            appliedWeapons++;

            if (recreateWeapons)
            {
                int clip1 = 0;
                int reserveAmmo = 0;
                bool restoreAmmo = !isKnife;
                if (restoreAmmo)
                {
                    try
                    {
                        CCSWeaponBaseGun gun = weapon.As<CCSWeaponBaseGun>();
                        clip1 = gun.Clip1;
                        reserveAmmo = gun.ReserveAmmo[0];
                    }
                    catch
                    {
                        restoreAmmo = false;
                    }
                }

                weaponsToRecreate.Add(new RaitoWeaponRefreshState(weapon, weaponClass, clip1, reserveAmmo, restoreAmmo));
            }
        }

        if (diagnostic)
        {
            Log($"[BETHECHAMP] Skin refresh found {appliedWeapons} matching weapon entities: {(matchedWeapons.Count == 0 ? "none" : string.Join(", ", matchedWeapons))}.");
        }

        if (weaponsToRecreate.Count > 0)
        {
            ScheduleRaitoWeaponRecreation(player, weaponsToRecreate, diagnostic);
        }
    }

    private bool ApplyRaitoSkinToWeapon(
        CCSPlayerController player,
        CBasePlayerWeapon weapon,
        Dictionary<string, RaitoSkinRecord> skins,
        string side,
        out string weaponClass,
        out bool isKnife,
        out RaitoSkinRecord? skin)
    {
        weaponClass = weapon.DesignerName.Trim().ToLowerInvariant();
        isKnife = weaponClass.Contains("knife", StringComparison.OrdinalIgnoreCase) || weaponClass.Contains("bayonet", StringComparison.OrdinalIgnoreCase);
        string itemType = isKnife ? "KNIFE" : "WEAPON";
        string slotKey = isKnife ? "knife" : weaponClass;
        if (!TryGetRaitoSkin(skins, side, itemType, slotKey, out skin) || skin is null || skin.PaintKitId <= 0) return false;

        try
        {
            if (isKnife && skin.ItemDefIndex is > 0)
            {
                ushort definitionIndex = (ushort)skin.ItemDefIndex.Value;
                if (weapon.AttributeManager.Item.ItemDefinitionIndex != definitionIndex)
                {
                    weapon.AcceptInput("ChangeSubclass", value: definitionIndex.ToString());
                }

                weapon.AttributeManager.Item.ItemDefinitionIndex = definitionIndex;
                weapon.AttributeManager.Item.EntityQuality = 3;
            }
            else
            {
                weapon.AttributeManager.Item.EntityQuality = 0;
            }

            weapon.AttributeManager.Item.AttributeList.Attributes.RemoveAll();
            weapon.AttributeManager.Item.NetworkedDynamicAttributes.Attributes.RemoveAll();
            weapon.AttributeManager.Item.AccountID = (uint)player.SteamID;
            weapon.FallbackPaintKit = skin.PaintKitId;
            weapon.FallbackSeed = skin.Seed;
            weapon.FallbackWear = skin.Wear;
            weapon.AttributeManager.Item.CustomName = skin.NameTag;
            weapon.AttributeManager.Item.Initialized = true;
            UpdateRaitoEconItemId(weapon.AttributeManager.Item);
            SetRaitoAttribute(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle, "set item texture prefab", skin.PaintKitId);
            SetRaitoAttribute(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle, "set item texture seed", skin.Seed);
            SetRaitoAttribute(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle, "set item texture wear", skin.Wear);
            SetRaitoAttribute(weapon.AttributeManager.Item.AttributeList.Handle, "set item texture prefab", skin.PaintKitId);
            SetRaitoAttribute(weapon.AttributeManager.Item.AttributeList.Handle, "set item texture seed", skin.Seed);
            SetRaitoAttribute(weapon.AttributeManager.Item.AttributeList.Handle, "set item texture wear", skin.Wear);
            ApplyRaitoStickerAttributes(weapon.AttributeManager.Item, skin);
            weapon.AcceptInput("SetBodygroup", value: $"body,{(skin.LegacyModel ? 1 : 0)}");
            return true;
        }
        catch (Exception ex)
        {
            Log($"[BETHECHAMP] Skin apply failed for {weaponClass}: {ex.Message}");
            return false;
        }
    }

    private HookResult OnRaitoGiveNamedItemPost(DynamicHook hook)
    {
        try
        {
            CCSPlayer_ItemServices itemServices = hook.GetParam<CCSPlayer_ItemServices>(0);
            CBasePlayerWeapon weapon = hook.GetReturn<CBasePlayerWeapon>();
            if (weapon is null || !weapon.IsValid || !weapon.DesignerName.Contains("weapon", StringComparison.OrdinalIgnoreCase)) return HookResult.Continue;

            CBasePlayerPawn? pawn = itemServices.Pawn.Value;
            if (pawn is null || !pawn.IsValid || !pawn.Controller.IsValid || pawn.Controller.Value is null) return HookResult.Continue;

            CCSPlayerController player = new(pawn.Controller.Value.Handle);
            if (!IsPlayerValid(player)) return HookResult.Continue;

            Dictionary<string, RaitoSkinRecord>? skins;
            lock (raitoSkinCacheLock) raitoSkinCache.TryGetValue(player.SteamID, out skins);
            if (skins is null || skins.Count == 0) return HookResult.Continue;

            string side = player.TeamNum == (byte)CsTeam.CounterTerrorist ? "CT" : player.TeamNum == (byte)CsTeam.Terrorist ? "T" : "ALL";
            ApplyRaitoSkinToWeapon(player, weapon, skins, side, out _, out _, out _);
        }
        catch (Exception ex)
        {
            Log($"[BETHECHAMP] GiveNamedItem skin hook failed: {ex.Message}");
        }

        return HookResult.Continue;
    }

    private void ScheduleRaitoWeaponRecreation(CCSPlayerController player, IReadOnlyList<RaitoWeaponRefreshState> weapons, bool diagnostic)
    {
        foreach (RaitoWeaponRefreshState state in weapons)
        {
            if (state.Weapon.IsValid)
            {
                state.Weapon.AddEntityIOEvent("Kill", state.Weapon, null, "", 0.1f);
            }
        }

        AddTimer(0.23f, () =>
        {
            if (!IsPlayerValid(player) || player.PlayerPawn.Value?.WeaponServices is null) return;

            foreach (RaitoWeaponRefreshState state in weapons)
            {
                try
                {
                    CBasePlayerWeapon replacement = new(player.GiveNamedItem(state.DesignerName));
                    if (!replacement.IsValid) continue;

                    if (state.RestoreAmmo)
                    {
                        CCSWeaponBaseGun gun = replacement.As<CCSWeaponBaseGun>();
                        gun.Clip1 = state.Clip1;
                        gun.ReserveAmmo[0] = state.ReserveAmmo;
                    }
                }
                catch (Exception ex)
                {
                    Log($"[BETHECHAMP] Weapon recreation failed for {state.DesignerName}: {ex.Message}");
                }
            }

            if (diagnostic)
            {
                Log($"[BETHECHAMP] Recreated {weapons.Count} weapon entities; applying skins to the new entities.");
            }

            AddTimer(0.15f, () => ApplyRaitoSkins(player), TimerFlags.STOP_ON_MAPCHANGE);
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private static bool TryGetRaitoSkin(Dictionary<string, RaitoSkinRecord> skins, string side, string itemType, string weaponClass, out RaitoSkinRecord? skin)
    {
        if (skins.TryGetValue(RaitoSkinRecord.BuildKey(side, itemType, weaponClass), out skin)) return true;
        return skins.TryGetValue(RaitoSkinRecord.BuildKey("ALL", itemType, weaponClass), out skin);
    }

    private void InitializeRaitoGloveLoadoutHook()
    {
        try
        {
            raitoGetItemInLoadout = new MemoryFunctionWithReturn<nint, int, int, nint>(GameData.GetSignature("CCSPlayerInventory::GetItemInLoadout"));
            raitoSendInventoryUpdate = new MemoryFunctionVoid<nint>(GameData.GetSignature("CCSPlayerInventory::SendInventoryUpdateEvent"));
            raitoSetWearables = new MemoryFunctionVoid<nint>(GameData.GetSignature("CCSPlayer_ItemServices::SetWearables"));
            raitoSetModelFromLoadout = new MemoryFunctionWithReturn<nint, nint>(GameData.GetSignature("CCSPlayerPawn::SetModelFromLoadout"));
            raitoSetModelFromClass = new MemoryFunctionVoid<nint>(GameData.GetSignature("CCSPlayerPawn::SetModelFromClass"));
            raitoEconItemViewConstructor = new MemoryFunctionWithReturn<nint, nint>(GameData.GetSignature("CEconItemView::CEconItemView"));
            raitoEconItemViewCopy = new MemoryFunctionWithReturn<nint, nint, nint>(GameData.GetSignature("CEconItemView::operator="));
            raitoGetItemInLoadout.Hook(OnRaitoGetItemInLoadout, HookMode.Post);
            raitoGloveLoadoutHookInstalled = true;
            Log("[BETHECHAMP] Glove loadout integration initialized.");
        }
        catch (Exception ex)
        {
            raitoGetItemInLoadout = null;
            raitoSendInventoryUpdate = null;
            raitoSetWearables = null;
            raitoSetModelFromLoadout = null;
            raitoSetModelFromClass = null;
            raitoEconItemViewConstructor = null;
            raitoEconItemViewCopy = null;
            Log($"[BETHECHAMP] Glove loadout integration is unavailable; using the legacy refresh path: {ex.Message}");
        }
    }

    private HookResult OnRaitoGetItemInLoadout(DynamicHook hook)
    {
        if (hook.GetParam<int>(2) != RaitoGloveLoadoutSlot) return HookResult.Continue;

        nint inventoryHandle = hook.GetParam<nint>(0);
        nint sourceItemView = hook.GetReturn<nint>();
        if (inventoryHandle == nint.Zero || sourceItemView == nint.Zero) return HookResult.Continue;

        CCSPlayerController? player = FindRaitoPlayerByInventoryHandle(inventoryHandle);
        if (!IsPlayerValid(player)) return HookResult.Continue;

        int team = hook.GetParam<int>(1);
        string side = team == (int)CsTeam.CounterTerrorist ? "CT" : team == (int)CsTeam.Terrorist ? "T" : "ALL";
        Dictionary<string, RaitoSkinRecord>? skins;
        lock (raitoSkinCacheLock) raitoSkinCache.TryGetValue(player!.SteamID, out skins);
        if (skins is null || !TryGetRaitoSkin(skins, side, "GLOVE", "glove", out RaitoSkinRecord? skin) || skin?.ItemDefIndex is not > 0)
        {
            return HookResult.Continue;
        }

        try
        {
            CEconItemView itemView = GetOrCreateRaitoGloveItemView(player.SteamID, team, sourceItemView);
            ApplyRaitoGloveItemView(player, itemView, skin);
            hook.SetReturn(itemView.Handle);
            Interlocked.Increment(ref raitoGloveLoadoutHookHits);
            return HookResult.Changed;
        }
        catch (Exception ex)
        {
            Log($"[BETHECHAMP] Glove loadout hook failed for {player.SteamID}: {ex.Message}");
            return HookResult.Continue;
        }
    }

    private static CCSPlayerController? FindRaitoPlayerByInventoryHandle(nint inventoryHandle)
    {
        foreach (CCSPlayerController candidate in Utilities.GetPlayers())
        {
            CCSPlayerController_InventoryServices? inventoryServices = candidate.InventoryServices;
            if (candidate.IsValid && inventoryServices is not null && inventoryServices.Handle != nint.Zero &&
                inventoryServices.Handle + RaitoInventoryServicesInventoryOffset == inventoryHandle)
            {
                return candidate;
            }
        }

        return null;
    }

    private CEconItemView GetOrCreateRaitoGloveItemView(ulong steamId, int team, nint sourceItemView)
    {
        if (raitoEconItemViewConstructor is null || raitoEconItemViewCopy is null)
        {
            throw new InvalidOperationException("The glove item-view functions are not initialized.");
        }

        var key = (steamId, team);
        if (!raitoGloveItemViews.TryGetValue(key, out nint itemViewHandle))
        {
            itemViewHandle = Marshal.AllocHGlobal(Schema.GetClassSize("CEconItemView"));
            try
            {
                raitoEconItemViewConstructor.Invoke(itemViewHandle);
                raitoGloveItemViews[key] = itemViewHandle;
            }
            catch
            {
                Marshal.FreeHGlobal(itemViewHandle);
                throw;
            }
        }

        raitoEconItemViewCopy.Invoke(itemViewHandle, sourceItemView);
        return new CEconItemView(itemViewHandle);
    }

    private void ApplyRaitoGloveItemView(CCSPlayerController player, CEconItemView itemView, RaitoSkinRecord skin)
    {
        itemView.Initialized = true;
        itemView.ItemDefinitionIndex = (ushort)skin.ItemDefIndex!.Value;
        itemView.EntityQuality = 4;
        itemView.AccountID = (uint)player.SteamID;
        itemView.CustomName = skin.NameTag;
        UpdateRaitoEconItemId(itemView);
        itemView.NetworkedDynamicAttributes.Attributes.RemoveAll();
        SetRaitoAttribute(itemView.NetworkedDynamicAttributes.Handle, "set item texture prefab", skin.PaintKitId);
        SetRaitoAttribute(itemView.NetworkedDynamicAttributes.Handle, "set item texture seed", skin.Seed);
        SetRaitoAttribute(itemView.NetworkedDynamicAttributes.Handle, "set item texture wear", skin.Wear);
        itemView.AttributeList.Attributes.RemoveAll();
        SetRaitoAttribute(itemView.AttributeList.Handle, "set item texture prefab", skin.PaintKitId);
        SetRaitoAttribute(itemView.AttributeList.Handle, "set item texture seed", skin.Seed);
        SetRaitoAttribute(itemView.AttributeList.Handle, "set item texture wear", skin.Wear);
    }

    private void ApplyRaitoGloves(CCSPlayerController player, Dictionary<string, RaitoSkinRecord> skins, string side, bool diagnostic = false)
    {
        if (!TryGetRaitoSkin(skins, side, "GLOVE", "glove", out RaitoSkinRecord? skin) || skin?.ItemDefIndex is not > 0) return;
        if (player.PlayerPawn.Value is not { IsValid: true } pawn) return;

        if (raitoGloveLoadoutHookInstalled &&
            raitoSendInventoryUpdate is not null &&
            raitoSetWearables is not null &&
            raitoSetModelFromLoadout is not null &&
            raitoSetModelFromClass is not null &&
            player.InventoryServices is { } inventoryServices &&
            pawn.ItemServices?.As<CCSPlayer_ItemServices>() is { } itemServices)
        {
            player.ExecuteClientCommand("lastinv");
            AddTimer(0.08f, () =>
            {
                Server.NextWorldUpdate(() =>
                {
                    if (!IsPlayerValid(player) || !pawn.IsValid || itemServices.Handle == nint.Zero || inventoryServices.Handle == nint.Zero) return;

                    try
                    {
                        long hookHitsBefore = Interlocked.Read(ref raitoGloveLoadoutHookHits);
                        nint inventoryHandle = inventoryServices.Handle + RaitoInventoryServicesInventoryOffset;
                        raitoSendInventoryUpdate.Invoke(inventoryHandle);
                        raitoSetModelFromLoadout.Invoke(pawn.Handle);
                        raitoSetModelFromClass.Invoke(pawn.Handle);
                        pawn.AcceptInput("SetBodygroup", value: "default_gloves,1");
                        raitoSetWearables.Invoke(itemServices.Handle);
                        long hookHits = Interlocked.Read(ref raitoGloveLoadoutHookHits) - hookHitsBefore;
                        bool pawnGlovesSynced = SyncRaitoPawnGloves(player, pawn);
                        pawn.AcceptInput("SetBodygroup", value: "first_or_third_person,0");
                        Server.NextWorldUpdate(() =>
                        {
                            if (!pawn.IsValid) return;
                            pawn.AcceptInput("SetBodygroup", value: "first_or_third_person,1");
                            if (diagnostic)
                            {
                                Log($"[BETHECHAMP] Glove entity state after rebuild: {DescribeRaitoGloveState(pawn)}");
                            }
                        });
                        player.ExecuteClientCommand("lastinv");

                        if (diagnostic)
                        {
                            Log($"[BETHECHAMP] Refreshed inventory and rebuilt glove wearable definition {skin.ItemDefIndex.Value} with paint kit {skin.PaintKitId} for side {side}; intercepted {hookHits} loadout request(s), pawn sync {(pawnGlovesSynced ? "succeeded" : "failed")}.");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"[BETHECHAMP] Glove wearable rebuild failed: {ex.Message}");
                    }
                });
            }, TimerFlags.STOP_ON_MAPCHANGE);
            return;
        }

        ApplyRaitoGlovesLegacy(player, pawn, skin, side, diagnostic);
    }

    private bool SyncRaitoPawnGloves(CCSPlayerController player, CCSPlayerPawn pawn)
    {
        if (raitoEconItemViewCopy is null ||
            !raitoGloveItemViews.TryGetValue((player.SteamID, player.TeamNum), out nint itemViewHandle) ||
            itemViewHandle == nint.Zero)
        {
            return false;
        }

        raitoEconItemViewCopy.Invoke(pawn.EconGloves.Handle, itemViewHandle);
        unchecked
        {
            pawn.EconGlovesChanged++;
        }
        Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_nEconGlovesChanged");
        return true;
    }

    private static string DescribeRaitoGloveState(CCSPlayerPawn pawn)
    {
        List<string> wearables = new();
        foreach (CHandle<CEconWearable> wearableHandle in pawn.MyWearables)
        {
            CEconWearable? wearable = wearableHandle.Value;
            if (wearable is null || !wearable.IsValid) continue;
            wearables.Add($"{wearable.DesignerName}:{wearable.AttributeManager.Item.ItemDefinitionIndex}");
        }

        return $"pawn item definition {pawn.EconGloves.ItemDefinitionIndex}, change counter {pawn.EconGlovesChanged}, wearables [{(wearables.Count == 0 ? "none" : string.Join(", ", wearables))}]";
    }

    private void ApplyRaitoGlovesLegacy(CCSPlayerController player, CCSPlayerPawn pawn, RaitoSkinRecord skin, string side, bool diagnostic)
    {
        CEconItemView gloves = pawn.EconGloves;
        gloves.NetworkedDynamicAttributes.Attributes.RemoveAll();
        gloves.AttributeList.Attributes.RemoveAll();
        player.ExecuteClientCommand("lastinv");
        AddTimer(0.08f, () =>
        {
            if (!IsPlayerValid(player) || !pawn.IsValid) return;

            try
            {
                ushort definitionIndex = (ushort)skin.ItemDefIndex!.Value;
                gloves.ItemDefinitionIndex = definitionIndex;
                UpdateRaitoEconItemId(gloves);
                gloves.NetworkedDynamicAttributes.Attributes.RemoveAll();
                SetRaitoAttribute(gloves.NetworkedDynamicAttributes.Handle, "set item texture prefab", skin.PaintKitId);
                SetRaitoAttribute(gloves.NetworkedDynamicAttributes.Handle, "set item texture seed", skin.Seed);
                SetRaitoAttribute(gloves.NetworkedDynamicAttributes.Handle, "set item texture wear", skin.Wear);
                gloves.AttributeList.Attributes.RemoveAll();
                SetRaitoAttribute(gloves.AttributeList.Handle, "set item texture prefab", skin.PaintKitId);
                SetRaitoAttribute(gloves.AttributeList.Handle, "set item texture seed", skin.Seed);
                SetRaitoAttribute(gloves.AttributeList.Handle, "set item texture wear", skin.Wear);
                gloves.Initialized = true;
                unchecked
                {
                    pawn.EconGlovesChanged++;
                }
                Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_nEconGlovesChanged");
                player.ExecuteClientCommand("lastinv");
                pawn.AcceptInput("SetBodygroup", value: "first_or_third_person,0");
                AddTimer(0.2f, () =>
                {
                    if (pawn.IsValid) pawn.AcceptInput("SetBodygroup", value: "first_or_third_person,1");
                }, TimerFlags.STOP_ON_MAPCHANGE);
                if (diagnostic)
                {
                    Log($"[BETHECHAMP] Applied legacy glove definition {definitionIndex} with paint kit {skin.PaintKitId} for side {side}; change counter is {pawn.EconGlovesChanged}.");
                }
            }
            catch (Exception ex)
            {
                Log($"[BETHECHAMP] Legacy glove apply failed: {ex.Message}");
            }
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void ClearRaitoGloveItemViews()
    {
        foreach (nint itemViewHandle in raitoGloveItemViews.Values)
        {
            if (itemViewHandle != nint.Zero) Marshal.FreeHGlobal(itemViewHandle);
        }
        raitoGloveItemViews.Clear();
    }

    private static void EnableRaitoSkinHud(CCSPlayerController player)
    {
        player.ExecuteClientCommand("cl_weapon_selection_rarity_color 1");
    }

    private void SetRaitoAttribute(nint handle, string name, object value)
    {
        if (raitoSetAttribute is null)
        {
            LogRaitoSignatureWarning();
            return;
        }

        try
        {
            raitoSetAttribute.Invoke(handle, name, value);
        }
        catch (Exception ex)
        {
            LogRaitoAttributeFailure(ex);
        }
    }

    private void ApplyRaitoStickerAttributes(CEconItemView itemView, RaitoSkinRecord skin)
    {
        if (!skin.ItemType.Equals("WEAPON", StringComparison.OrdinalIgnoreCase)) return;

        for (int slot = 0; slot < Math.Min(5, skin.Stickers.Count); slot++)
        {
            if (skin.Stickers[slot] is not int stickerId || stickerId <= 0) continue;
            float encodedStickerId = BitConverter.Int32BitsToSingle(stickerId);
            string attributeName = $"sticker slot {slot} id";
            SetRaitoAttribute(itemView.NetworkedDynamicAttributes.Handle, attributeName, encodedStickerId);
            SetRaitoAttribute(itemView.AttributeList.Handle, attributeName, encodedStickerId);
        }
    }

    private void LogRaitoSignatureWarning()
    {
        if (raitoSignatureWarningLogged) return;
        raitoSignatureWarningLogged = true;
        Log("[BETHECHAMP] Install gamedata/raito_weaponpaints.json in the global CounterStrikeSharp gamedata folder to enable knife and glove paint attributes.");
    }

    private void LogRaitoAttributeFailure(Exception ex)
    {
        if (raitoSignatureWarningLogged) return;
        raitoSignatureWarningLogged = true;
        Log($"[BETHECHAMP] Skin attribute update failed: {ex.Message}");
    }

    private void UpdateRaitoEconItemId(CEconItemView item)
    {
        ulong itemId = (ulong)Interlocked.Increment(ref nextRaitoItemId);
        item.ItemID = itemId;
        item.ItemIDLow = (uint)(itemId & uint.MaxValue);
        item.ItemIDHigh = (uint)(itemId >> 32);
    }

    private bool LoadRaitoSkinAttributePermission()
    {
        string path = Path.Combine(Server.GameDirectory, "csgo", "addons", "counterstrikesharp", "configs", "core.json");
        if (!File.Exists(path))
        {
            Log($"[BETHECHAMP] CounterStrikeSharp core config was not found at {path}; skin attributes are disabled.");
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("FollowCS2ServerGuidelines", out JsonElement property) || property.ValueKind != JsonValueKind.False)
            {
                Log("[BETHECHAMP] Skin attributes are disabled because FollowCS2ServerGuidelines is not false.");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Log($"[BETHECHAMP] Could not read CounterStrikeSharp core config; skin attributes are disabled: {ex.Message}");
            return false;
        }
    }

    private void OnRaitoClientAuthorized(int playerSlot, ulong steamId64)
    {
        if (raitoDatabase is null) return;

        try
        {
            var ban = raitoDatabase.GetActiveModeration("Ban", steamId64);
            if (ban is not null)
            {
                string reason = SanitizeServerCommandText(string.IsNullOrWhiteSpace(ban.Reason) ? "Banned by BETHECHAMP admin" : ban.Reason);
                Server.NextFrame(() => Server.ExecuteCommand($"kickid {playerSlot + 1} \"Banned: {reason}\""));
                return;
            }
            AddTimer(1.0f, () => EnforceRaitoReservedSlot(steamId64), TimerFlags.STOP_ON_MAPCHANGE);
        }
        catch (Exception ex)
        {
            Log($"[BETHECHAMP] Ban check failed for {steamId64}: {ex.Message}");
        }
    }

    private void PushRaitoHeartbeat()
    {
        if (raitoDatabase is null) return;

        try
        {
            int currentPlayers = Math.Min(raitoConfig.PublicSlots, Utilities.GetPlayers().Count(player => player.IsValid && !player.IsBot && !player.IsHLTV && player.TeamNum != (byte)CsTeam.Spectator));
            raitoDatabase.ExpireAdminCallsForMapChange(raitoConfig.ServerId, Server.MapName);
            NotifyRaitoAdminCallUpdates();
            string state = isRaitoTestMatch ? "test" : isPractice ? "practice" : isMatchLive ? "live" : isKnifeRound ? "knife" : isWarmup ? "warmup" : "waiting";
            raitoDatabase.UpdateServer(raitoConfig, currentPlayers, state, Server.MapName);
        }
        catch (Exception ex)
        {
            Log($"[BETHECHAMP] Heartbeat failed: {ex.Message}");
        }
    }

    private void NotifyRaitoAdminCallUpdates()
    {
        if (raitoDatabase is null) return;
        foreach (RaitoAdminCallUpdate update in raitoDatabase.GetRecentAdminCallUpdates(raitoConfig.ServerId))
        {
            if (!raitoAdminCallStatuses.TryGetValue(update.Id, out string? previous))
            {
                raitoAdminCallStatuses[update.Id] = update.Status;
                continue;
            }
            if (previous == update.Status) continue;
            raitoAdminCallStatuses[update.Id] = update.Status;
            CCSPlayerController? caller = Utilities.GetPlayers().FirstOrDefault(player => player.IsValid && player.SteamID == update.CallerSteamId64);
            if (!IsPlayerValid(caller)) continue;
            if (update.Status == "CLAIMED") PrintToPlayerChat(caller!, "An administrator accepted your call.");
            else if (update.Status is "RESOLVED" or "DISMISSED" or "EXPIRED") PrintToPlayerChat(caller!, $"Your admin call is now {update.Status.ToLowerInvariant()}.");
        }
    }

    private void PollRaitoGameCommands()
    {
        RaitoDatabase? database = raitoDatabase;
        if (database is null || Interlocked.CompareExchange(ref raitoCommandPollInProgress, 1, 0) != 0) return;
        string serverId = raitoConfig.ServerId;

        _ = Task.Run(() => database.GetPendingGameCommands(serverId)).ContinueWith(task =>
        {
            try
            {
                Server.NextFrame(() =>
                {
                    try
                    {
                        if (task.IsFaulted)
                        {
                            Log($"[BETHECHAMP] Game command polling failed: {task.Exception?.GetBaseException().Message}");
                            return;
                        }

                        foreach (RaitoGameCommand command in task.Result)
                        {
                            ProcessRaitoGameCommand(command);
                        }
                    }
                    finally
                    {
                        Interlocked.Exchange(ref raitoCommandPollInProgress, 0);
                    }
                });
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref raitoCommandPollInProgress, 0);
                Log($"[BETHECHAMP] Could not schedule game command processing: {ex.Message}");
            }
        }, TaskScheduler.Default);
    }

    private void ProcessRaitoGameCommand(RaitoGameCommand command)
    {
        if (raitoDatabase is null) return;
        string status = "APPLIED";
        string detail;
        try
        {
            CCSPlayerController? player = Utilities.GetPlayers().FirstOrDefault(candidate =>
                candidate.IsValid && !candidate.IsBot && !candidate.IsHLTV && candidate.SteamID == command.TargetSteamId64);

            switch (command.Type.ToUpperInvariant())
            {
                case "BAN":
                    if (player is null)
                    {
                        status = "SKIPPED";
                        detail = "Target was not connected; the global ban remains enforced on authorization.";
                    }
                    else
                    {
                        KickTarget(player, $"Banned: {command.Reason}");
                        detail = $"Kicked {player.PlayerName} and retained the global ban.";
                    }
                    break;
                case "KICK":
                    if (player is null)
                    {
                        status = "SKIPPED";
                        detail = "Target was not connected to this server.";
                    }
                    else
                    {
                        KickTarget(player, command.Reason);
                        detail = $"Kicked {player.PlayerName}.";
                    }
                    break;
                case "MUTE":
                    if (player is null)
                    {
                        status = "SKIPPED";
                        detail = "Target was not connected; chat mute enforcement is stored globally.";
                    }
                    else
                    {
                        PrintToPlayerChat(player, "Your BETHECHAMP chat access has been suspended.");
                        detail = $"Notified {player.PlayerName}; chat checks now enforce the mute.";
                    }
                    break;
                case "UNMUTE":
                    if (player is null)
                    {
                        status = "SKIPPED";
                        detail = "Target was not connected; the global mute record is already removed.";
                    }
                    else
                    {
                        PrintToPlayerChat(player, "Your BETHECHAMP chat access has been restored.");
                        detail = $"Notified {player.PlayerName}.";
                    }
                    break;
                case "GAG":
                    if (player is null)
                    {
                        status = "SKIPPED";
                        detail = "Target was not connected; voice gag enforcement is stored globally.";
                    }
                    else
                    {
                        player.VoiceFlags = VoiceFlags.Muted;
                        PrintToPlayerChat(player, "Your BETHECHAMP voice access has been suspended.");
                        detail = $"Applied voice gag to {player.PlayerName}.";
                    }
                    break;
                case "UNGAG":
                    if (player is null)
                    {
                        status = "SKIPPED";
                        detail = "Target was not connected; the global gag record is already removed.";
                    }
                    else
                    {
                        player.VoiceFlags = VoiceFlags.Normal;
                        PrintToPlayerChat(player, "Your BETHECHAMP voice access has been restored.");
                        detail = $"Cleared voice gag for {player.PlayerName}.";
                    }
                    break;
                case "UNBAN":
                    detail = "The global ban record is removed; no live entity action is required.";
                    break;
                default:
                    status = "FAILED";
                    detail = $"Unsupported game command type {command.Type}.";
                    break;
            }

            raitoDatabase.AddGameCommandReceipt(command.Id, raitoConfig.ServerId, status, detail);
            Log($"[BETHECHAMP] Discord game command {command.Type} for {command.TargetSteamId64}: {status} - {detail}");
        }
        catch (Exception ex)
        {
            Log($"[BETHECHAMP] Game command {command.Id} failed: {ex.Message}");
            try
            {
                raitoDatabase.AddGameCommandReceipt(command.Id, raitoConfig.ServerId, "FAILED", ex.Message);
            }
            catch (Exception receiptError)
            {
                Log($"[BETHECHAMP] Could not acknowledge failed game command {command.Id}: {receiptError.Message}");
            }
        }
    }

    internal bool RaitoCanExecute(CCSPlayerController? player, string command, params string[] permissions)
    {
        if (player is null) return true;
        if (loadedAdmins.ContainsKey(player.SteamID.ToString())) return true;
        if (raitoDatabase is null) return false;

        string role = GetRaitoRole(player.SteamID);
        if (role == "OWNER") return true;

        string normalizedCommand = command.ToLowerInvariant();
        if (normalizedCommand is "css_ban" or "css_unban") return role is "MANAGER" or "HEAD_ADMIN" or "VIP" or "ADMIN";
        if (normalizedCommand is "css_mute" or "css_unmute" or "css_gag" or "css_ungag") return role is "MANAGER" or "HEAD_ADMIN" or "VIP" or "ADMIN";
        if (normalizedCommand is "css_kick" or "css_team") return role is "HEAD_ADMIN" or "VIP" or "ADMIN";
        if (normalizedCommand is "css_start" or "css_force" or "css_forcestart" or "css_restart" or "css_rr" or "css_endmatch" or "css_forceend") return role is "HEAD_ADMIN" or "VIP" or "ADMIN";
        if (permissions.Contains("@css/root", StringComparer.OrdinalIgnoreCase)) return false;
        if (permissions.Contains("@css/config", StringComparer.OrdinalIgnoreCase)) return role is "MANAGER" or "HEAD_ADMIN";
        if (permissions.Contains("@css/map", StringComparer.OrdinalIgnoreCase) || permissions.Contains("@custom/prac", StringComparer.OrdinalIgnoreCase)) return role is "MANAGER" or "HEAD_ADMIN" or "VIP" or "ADMIN";
        if (permissions.Contains("@css/chat", StringComparer.OrdinalIgnoreCase)) return role is "MANAGER" or "HEAD_ADMIN" or "VIP" or "ADMIN";
        return role is "MANAGER" or "HEAD_ADMIN" or "VIP" or "ADMIN";
    }

    internal string GetRaitoRole(ulong steamId64)
    {
        if (loadedAdmins.ContainsKey(steamId64.ToString()))
        {
            return "OWNER";
        }

        RefreshRaitoRoleCacheInvalidation();
        if (raitoRoleCache.TryGetValue(steamId64, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
        {
            return cached.Role;
        }

        if (raitoDatabase is null || !raitoDatabase.TryGetUserRole(steamId64, out string role, out _))
        {
            return "USER";
        }

        raitoRoleCache[steamId64] = (role, DateTime.UtcNow.AddSeconds(15));
        return role;
    }

    internal string GetRaitoRoleFresh(ulong steamId64)
    {
        if (loadedAdmins.ContainsKey(steamId64.ToString())) return "OWNER";
        if (raitoDatabase is null || !raitoDatabase.TryGetUserRole(steamId64, out string role, out _)) return "USER";
        raitoRoleCache[steamId64] = (role, DateTime.UtcNow.AddSeconds(15));
        return role;
    }

    private void RefreshRaitoRoleCacheInvalidation()
    {
        if (raitoDatabase is null) return;
        DateTime invalidatedAt = raitoDatabase.GetRoleCacheInvalidationTimestamp();
        if (invalidatedAt <= raitoRoleCacheInvalidatedAt) return;
        raitoRoleCache.Clear();
        raitoRoleCacheInvalidatedAt = invalidatedAt;
    }

    internal string GetRaitoCachedRole(ulong steamId64)
        => loadedAdmins.ContainsKey(steamId64.ToString()) ? "OWNER" : raitoRoleCache.TryGetValue(steamId64, out var cached) ? cached.Role : "USER";

    private static string NormalizeRaitoSayMessage(string rawMessage)
    {
        string message = rawMessage.Trim();
        if (message.Length >= 2 && message[0] == '"' && message[^1] == '"')
        {
            message = message[1..^1];
        }

        return message.Trim();
    }

    internal void WriteRaitoAdminLog(CCSPlayerController? actor, string action, RaitoTarget? target = null, string? reason = null, object? metadata = null)
    {
        if (raitoDatabase is null) return;
        try
        {
            raitoDatabase.AddAdminLog(actor?.SteamID, actor?.PlayerName ?? "Console", action, target?.SteamId64, target?.PlayerName, reason, metadata);
        }
        catch (Exception ex)
        {
            Log($"[BETHECHAMP] Admin log failed for {action}: {ex.Message}");
        }
    }

    private static string SanitizeServerCommandText(string value) => value.Replace("\"", "'").Replace("\r", " ").Replace("\n", " ").Trim();
}
