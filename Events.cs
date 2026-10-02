using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using Microsoft.Extensions.Logging;

namespace WeaponPaints
{
	public partial class WeaponPaints
	{
		private bool _mvpPlayed;
		private bool _giveNamedItemHooked;
		
		[GameEventHandler]
		public HookResult OnClientFullConnect(EventPlayerConnectFull @event, GameEventInfo info)
     	{
			CCSPlayerController? player = @event.Userid;

			if (player is null || !player.IsValid || player.IsBot ||
				WeaponSync == null || Database == null) return HookResult.Continue;

			var playerInfo = new PlayerInfo
			{
				UserId = player.UserId,
				Slot = player.Slot,
				Index = (int)player.Index,
				SteamId = player.SteamID.ToString(),
				Name = player.PlayerName,
				IpAddress = player.IpAddress?.Split(":")[0]
			};

			try
			{
				// LANN: retry a load that could not reach the database (5, 10, 20, 40 s). Without it the player played the whole
				// map without skins until !wp or a reconnect (PUBLIC #1, map-change reconnect bursts, 2026-10-09..14, T-102).
				// A disconnect, or another player taking the slot, cancels the pending retries.
				long token = Interlocked.Increment(ref _loadSeq);
				_loadToken[playerInfo.Slot] = token;
				_ = Task.Run(async () =>
				{
					for (int attempt = 0; ; attempt++)
					{
						if (await WeaponSync.GetPlayerData(playerInfo)) break;
						if (attempt >= 4)
						{
							Logger.LogWarning($"Gave up loading {playerInfo.SteamId} after {attempt + 1} attempts; !wp or a reconnect loads it");
							return;
						}
						await Task.Delay(TimeSpan.FromSeconds(5 << attempt));
						if (!_loadToken.TryGetValue(playerInfo.Slot, out var t) || t != token) return;
					}
					if (_loadToken.TryGetValue(playerInfo.Slot, out var t2) && t2 == token)
						Server.NextFrame(() => RefreshAfterLoad(playerInfo));
				});
				/*
				if (Config.Additional.SkinEnabled)
				{
					_ = Task.Run(async () => await weaponSync.GetWeaponPaintsFromDatabase(playerInfo));
				}
				if (Config.Additional.KnifeEnabled)
				{
					_ = Task.Run(async () => await weaponSync.GetKnifeFromDatabase(playerInfo));
				}
				if (Config.Additional.GloveEnabled)
				{
					_ = Task.Run(async () => await weaponSync.GetGloveFromDatabase(playerInfo));
				}
				if (Config.Additional.AgentEnabled)
				{
					_ = Task.Run(async () => await weaponSync.GetAgentFromDatabase(playerInfo));
				}
				if (Config.Additional.MusicEnabled)
				{
					_ = Task.Run(async () => await weaponSync.GetMusicFromDatabase(playerInfo));
				}
				*/
			}
			catch
			{
			}
			
			Players.Add(player);

			return HookResult.Continue;
		}

		[GameEventHandler]
		public HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
		{
			CCSPlayerController? player = @event.Userid;

			if (player is null || !player.IsValid || player.IsBot) return HookResult.Continue;

			_refreshAfterLoadPending.Remove(player.Slot);
			_loadToken.TryRemove(player.Slot, out _);

			var playerInfo = new PlayerInfo
			{
				UserId = player.UserId,
				Slot = player.Slot,
				Index = (int)player.Index,
				SteamId = player.SteamID.ToString(),
				Name = player.PlayerName,
				IpAddress = player.IpAddress?.Split(":")[0]
			};

			Task.Run(async () => 
			{
				if (WeaponSync != null)
					await WeaponSync.SyncStatTrakToDatabase(playerInfo);

				if (Config.Additional.SkinEnabled)
				{
					GPlayerWeaponsInfo.TryRemove(player.Slot, out _);
				}
			});

			if (Config.Additional.KnifeEnabled)
			{
				GPlayersKnife.TryRemove(player.Slot, out _);
			}
			if (Config.Additional.GloveEnabled)
			{
				GPlayersGlove.TryRemove(player.Slot, out _);
			}
			if (Config.Additional.AgentEnabled)
			{
				GPlayersAgent.TryRemove(player.Slot, out _);
			}
			if (Config.Additional.MusicEnabled)
			{
				GPlayersMusic.TryRemove(player.Slot, out _);
			}
			if (Config.Additional.PinsEnabled)
			{
				GPlayersPin.TryRemove(player.Slot, out _);
			}
			
			_temporaryPlayerWeaponWear.TryRemove(player.Slot, out _);
			CommandsCooldown.Remove(player.Slot);
			Players.Remove(player);

			return HookResult.Continue;
		}

		private void OnMapStart(string mapName)
		{
			if (Config.Additional is { KnifeEnabled: false, SkinEnabled: false, GloveEnabled: false }) return;
			
			if (Database != null)
				WeaponSync = new WeaponSynchronization(Database, Config);

			_fadeSeed = 0;
			_nextItemId = MinimumCustomItemId;
			_refreshAfterLoadPending.Clear();
		}

		// LANN: a player who reconnects mid-match takes back a pawn that is already alive ("ClientPutInServer reconnecting
		// player controller"), holding weapons the engine gave while they were away - unpainted, because skins are only
		// applied when a weapon is given. Nothing re-gives them until that player dies, so the skins stayed off until !wp
		// (MATCH #40, 2026-09-29). Anyone who spawns before their data has loaded is in the same state. Once the data is in,
		// refresh a living player the way !wp does; between round end and round start (refresh blocked) wait for the round.
		private readonly Dictionary<int, string> _refreshAfterLoadPending = [];
		// per-slot load token for the retry loop in OnClientFullConnect (thread-safe: the loop runs off the main thread)
		private readonly System.Collections.Concurrent.ConcurrentDictionary<int, long> _loadToken = new();
		private long _loadSeq;

		private void RefreshAfterLoad(PlayerInfo info)
		{
			var player = Utilities.GetPlayerFromSlot(info.Slot);
			if (player is not { IsValid: true, IsBot: false } || player.SteamID.ToString() != info.SteamId) return;
			if ((LifeState_t)player.LifeState != LifeState_t.LIFE_ALIVE) return; // not spawned yet: the normal give paints it
			if (!_gBCommandsAllowed)
			{
				_refreshAfterLoadPending[info.Slot] = info.SteamId ?? "";
				return;
			}
			RefreshLikeWp(player);
		}

		private void RefreshLikeWp(CCSPlayerController player)
		{
			try
			{
				GivePlayerGloves(player);
				if (Config.Additional.SkinEnabled || Config.Additional.KnifeEnabled) RefreshWeapons(player);
				GivePlayerAgent(player);
				GivePlayerMusicKit(player);
				AddTimer(0.15f, () => { if (player.IsValid) GivePlayerPin(player); });
			}
			catch (Exception ex)
			{
				Logger.LogWarning("refresh after load failed: " + ex.Message);
			}
		}

		private HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
		{
			CCSPlayerController? player = @event.Userid;

			if (player is null || !player.IsValid || Config.Additional is { KnifeEnabled: false, GloveEnabled: false })
				return HookResult.Continue;

			CCSPlayerPawn? pawn = player.PlayerPawn.Value;

			if (pawn == null || !pawn.IsValid)
				return HookResult.Continue;

			GivePlayerMusicKit(player);
			GivePlayerAgent(player);
			Server.NextFrame(() =>
			{
				GivePlayerGloves(player);
			});
			GivePlayerPin(player);

			return HookResult.Continue;
		}

		private HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
		{
			_gBCommandsAllowed = false;
			return HookResult.Continue;
		}

		private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
		{
			_gBCommandsAllowed = true;
			_mvpPlayed = false;
			if (_refreshAfterLoadPending.Count > 0)
			{
				var pending = _refreshAfterLoadPending.ToArray();
				_refreshAfterLoadPending.Clear();
				foreach (var (slot, steamId) in pending)
				{
					var p = Utilities.GetPlayerFromSlot(slot);
					if (p is { IsValid: true, IsBot: false } && p.SteamID.ToString() == steamId &&
					    (LifeState_t)p.LifeState == LifeState_t.LIFE_ALIVE)
						RefreshLikeWp(p);
				}
			}
			return HookResult.Continue;
		}
		
		private HookResult OnRoundMvp(EventRoundMvp @event, GameEventInfo info)
		{
			if (_mvpPlayed)
				return HookResult.Continue;
			
			var player = @event.Userid;
			
			if (player == null || !player.IsValid || player.IsBot)
				return HookResult.Continue;

			if (!(GPlayersMusic.TryGetValue(player.Slot, out var musicInfo)
			      && musicInfo.TryGetValue(player.Team, out var musicId)
			      && musicId != 0))
				return HookResult.Continue;
					
			@event.Musickitid = musicId;
			@event.Nomusic = 0;
			info.DontBroadcast = true;
			
			var newEvent = new EventRoundMvp(true)
			{
				Userid = player,
				Musickitid = musicId,
				Nomusic = 0,
			};

			_mvpPlayed = true;
			
			newEvent.FireEvent(false);
			return HookResult.Continue;
		}

		private HookResult OnGiveNamedItemPost(DynamicHook hook)
		{
			// This runs inside a native DynoHook callback: a null dereference here is NOT a catchable
			// NullReferenceException, it is a SIGSEGV that kills the server (the try/catch below never runs).
			// GiveNamedItem returns NULL when the engine refuses the item - e.g. MatchZy's !restore makes the
			// engine re-give each player's backed-up inventory by designer name ("weapon_knife_karambit"),
			// which it refuses. So check the raw pointers before wrapping them in schema classes.
			try
			{
				var weaponPtr = hook.GetReturn<nint>();
				var itemServicesPtr = hook.GetParam<nint>(0);
				if (weaponPtr == 0 || itemServicesPtr == 0)
					return HookResult.Continue;

				var itemServices = new CCSPlayer_ItemServices(itemServicesPtr);
				var weapon = new CBasePlayerWeapon(weaponPtr);
				if (!weapon.IsValid || weapon.DesignerName is not { } designerName || !designerName.Contains("weapon"))
					return HookResult.Continue;

				var player = GetPlayerFromItemServices(itemServices);
				if (player != null)
				{
					GivePlayerWeaponSkin(player, weapon);
				}
			}
			catch { }

			return HookResult.Continue;
		}

		private void OnEntityCreated(CEntityInstance entity)
		{
			var designerName = entity.DesignerName;

			if (designerName != null && designerName.Contains("weapon"))
			{
				Server.NextWorldUpdate(() =>
				{
					var weapon = new CBasePlayerWeapon(entity.Handle);
					if (!weapon.IsValid) return;

					try
					{
						SteamID? steamid = null;

						if (weapon.OriginalOwnerXuidLow > 0)
							steamid = new SteamID(weapon.OriginalOwnerXuidLow);

						CCSPlayerController? player;

						if (steamid != null && steamid.IsValid())
						{
							player = Players.FirstOrDefault(p => p.IsValid && p.SteamID == steamid.SteamId64);

							if (player == null)
								player = Utilities.GetPlayerFromSteamId(weapon.OriginalOwnerXuidLow);
						}
						else
						{
							var ownerPawn = weapon.OwnerEntity.Value?.As<CCSPlayerPawn>();
							player = ownerPawn is { IsValid: true }
								? ownerPawn.Controller.Value?.As<CCSPlayerController>()
								: null;
						}

						if (string.IsNullOrEmpty(player?.PlayerName)) return;
						if (!Utility.IsPlayerValid(player)) return;
						
						GivePlayerWeaponSkin(player, weapon);
					}
					catch (Exception)
					{
					}
				});
			}
		}

		private void OnTick()
		{
			if (!Config.Additional.ShowSkinImage) return;

			foreach (var player in Players)
			{
				if (_playerWeaponImage.TryGetValue(player.Slot, out var value) && !string.IsNullOrEmpty(value))
				{
					player.PrintToCenterHtml("<img src='{PATH}'</img>".Replace("{PATH}", value));
				}
			}
		}
		
		[GameEventHandler]
		public HookResult OnItemPickup(EventItemPickup @event, GameEventInfo _)
		{
			// if (!IsWindows) return HookResult.Continue;
			var player = @event.Userid;
			if (player == null || !player.IsValid || player.IsBot) return HookResult.Continue;
			if (!@event.Item.Contains("knife")) return HookResult.Continue;
		
			var weaponDefIndex = (int)@event.Defindex;
				
			if (!HasChangedKnife(player, out var _) || !HasChangedPaint(player, weaponDefIndex, out var _))
				return HookResult.Continue;
			
			if (player is { Connected: PlayerConnectedState.Connected, PawnIsAlive: true, PlayerPawn.IsValid: true })
			{
				GiveOnItemPickup(player);
			}
		
			return HookResult.Continue;
		}

		private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
		{
			CCSPlayerController? player = @event.Attacker;
			CCSPlayerController? victim = @event.Userid;

			if (player is null || !player.IsValid)
				return HookResult.Continue;
			
			if (victim == null || !victim.IsValid || victim == player)
				return HookResult.Continue;
			
			CBasePlayerWeapon? weapon = player.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value;

			if (weapon == null) return HookResult.Continue;

			int weaponDefIndex = weapon.AttributeManager.Item.ItemDefinitionIndex;

			if (!HasChangedPaint(player, weaponDefIndex, out var weaponInfo) || weaponInfo == null)
				return HookResult.Continue;
				
			if (!weaponInfo.StatTrak) return HookResult.Continue;
			
			weaponInfo.StatTrakCount += 1;
				
			CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle, "kill eater", ViewAsFloat((uint)weaponInfo.StatTrakCount));
			CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle, "kill eater score type", 0);
				
			CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.AttributeList.Handle, "kill eater", ViewAsFloat((uint)weaponInfo.StatTrakCount));
			CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.AttributeList.Handle, "kill eater score type", 0);

			return HookResult.Continue;
		}

		private void RegisterListeners()
		{
			RegisterListener<Listeners.OnMapStart>(OnMapStart);

			RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
			RegisterEventHandler<EventRoundStart>(OnRoundStart);
			RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
			RegisterEventHandler<EventRoundMvp>(OnRoundMvp);
			RegisterListener<Listeners.OnEntitySpawned>(OnEntityCreated);
			RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);

			if (Config.Additional.ShowSkinImage)
				RegisterListener<Listeners.OnTick>(OnTick);

			VirtualFunctions.GiveNamedItemFunc.Hook(OnGiveNamedItemPost, HookMode.Post);
			_giveNamedItemHooked = true;
		}
	}
}