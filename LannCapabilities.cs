using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace WeaponPaints;

/// <summary>
/// LANN: a capability for the LANN HUD's finish picker (LannMenu 1.4), so a finish chosen on a card is equipped exactly
/// the way this plugin's own skins menu equips one (SetupSkinsMenu): stored in the player's loadout for the team, the
/// weapons re-given at once when the player is alive and commands are allowed, the database synced in the background.
/// Typed with CounterStrikeSharp and BCL types only, so the consumer needs no shared assembly:
/// <c>"lann:wp:paint"  Func&lt;CCSPlayerController, int, int, int, bool&gt; = (player, team, defindex, paint) -&gt; stored</c>.
/// team: 2 = T, 3 = CT, 0 = both; paint 0 = Default (no finish). Game thread only; never throws into the caller.
/// Two more for the knife and glove models (LannMenu 1.5), built on this plugin's own knife and gloves menus:
/// <c>"lann:wp:knife"  Func&lt;CCSPlayerController, int, string, bool&gt; = (player, team, knifeClass)</c> and
/// <c>"lann:wp:gloves" Func&lt;CCSPlayerController, int, int, int, bool&gt; = (player, team, gloveDefindex, paint)</c>.
/// And the agent (LannMenu 1.6), built on this plugin's own agents menu:
/// <c>"lann:wp:agent"  Func&lt;CCSPlayerController, int, string, bool&gt; = (player, team, model)</c>.
/// </summary>
public partial class WeaponPaints
{
	private const string LannPaintCapabilityName = "lann:wp:paint";
	private const string LannKnifeCapabilityName = "lann:wp:knife";
	private const string LannGlovesCapabilityName = "lann:wp:gloves";
	private const string LannAgentCapabilityName = "lann:wp:agent";
	private const double LannPaintCooldownSeconds = 1.0;
	private const double LannModelCooldownSeconds = 0.5;

	private static readonly PluginCapability<Func<CCSPlayerController, int, int, int, bool>> LannPaintCapability =
		new(LannPaintCapabilityName);

	private static readonly PluginCapability<Func<CCSPlayerController, int, string, bool>> LannKnifeCapability =
		new(LannKnifeCapabilityName);

	private static readonly PluginCapability<Func<CCSPlayerController, int, int, int, bool>> LannGlovesCapability =
		new(LannGlovesCapabilityName);

	private static readonly PluginCapability<Func<CCSPlayerController, int, string, bool>> LannAgentCapability =
		new(LannAgentCapabilityName);

	private static readonly ConcurrentDictionary<int, DateTime> LannPaintCooldown = new();
	private static readonly ConcurrentDictionary<int, DateTime> LannModelCooldown = new();

	private void RegisterLannCapabilities()
	{
		Capabilities.RegisterPluginCapability(LannPaintCapability, () => LannSetPaint);
		Capabilities.RegisterPluginCapability(LannKnifeCapability, () => LannSetKnife);
		Capabilities.RegisterPluginCapability(LannGlovesCapability, () => LannSetGloves);
		Capabilities.RegisterPluginCapability(LannAgentCapability, () => LannSetAgent);
	}

	private static CsTeam[] LannTeams(int team) =>
		team == 0 ? [CsTeam.Terrorist, CsTeam.CounterTerrorist] : [(CsTeam)team];

	private static PlayerInfo LannPlayerInfo(CCSPlayerController player) => new()
	{
		UserId = player.UserId,
		Slot = player.Slot,
		Index = (int)player.Index,
		SteamId = player.SteamID.ToString(),
		Name = player.PlayerName,
		IpAddress = player.IpAddress?.Split(":")[0]
	};

	private static bool LannCooldownPassed(ConcurrentDictionary<int, DateTime> cooldown, int slot, double seconds)
	{
		DateTime now = DateTime.UtcNow;
		if (cooldown.TryGetValue(slot, out DateTime until) && now < until)
		{
			return false;
		}

		cooldown[slot] = now.AddSeconds(seconds);
		return true;
	}

	/// <summary>
	/// The knife model, as this plugin's knife menu sets it (SetupKnifeMenu): <paramref name="knifeClass"/> must be one of
	/// its knives (weapon_knife*, weapon_bayonet; weapon_knife is the default knife) and the knife feature on. Stored for
	/// the team, weapons re-given when alive, the database synced in the background; the knife keeps its own finishes.
	/// </summary>
	private bool LannSetKnife(CCSPlayerController player, int team, string knifeClass)
	{
		try
		{
			if (!Utility.IsPlayerValid(player) || WeaponSync == null || !Config.Additional.KnifeEnabled ||
			    team is not (0 or 2 or 3) || string.IsNullOrEmpty(knifeClass) ||
			    !(knifeClass.StartsWith("weapon_knife") || knifeClass.StartsWith("weapon_bayonet")) ||
			    !WeaponList.ContainsKey(knifeClass) ||
			    !LannCooldownPassed(LannModelCooldown, player.Slot, LannModelCooldownSeconds))
			{
				return false;
			}

			CsTeam[] teams = LannTeams(team);
			var playerKnives = GPlayersKnife.GetOrAdd(player.Slot, _ => new ConcurrentDictionary<CsTeam, string>());
			foreach (CsTeam side in teams)
			{
				playerKnives[side] = knifeClass;
			}

			PlayerInfo playerInfo = LannPlayerInfo(player);
			if (_gBCommandsAllowed && (LifeState_t)player.LifeState == LifeState_t.LIFE_ALIVE)
			{
				RefreshWeapons(player);
			}

			_ = Task.Run(async () =>
			{
				try
				{
					await WeaponSync.SyncKnifeToDatabase(playerInfo, knifeClass, teams);
				}
				catch (Exception ex)
				{
					Utility.Log($"lann:wp:knife sync failed: {ex.Message}");
				}
			});
			return true;
		}
		catch (Exception ex)
		{
			Logger.LogWarning("lann:wp:knife failed: {Type}", ex.GetType().Name);
			return false;
		}
	}

	/// <summary>
	/// The agent, as this plugin's agents menu sets it (SetupAgentsMenu): <paramref name="model"/> is the agents file's model
	/// for <paramref name="team"/> (2 = T, 3 = CT; agents belong to one team), "null" or "" = the map's default agent. Stored
	/// for that team and synced to the database in the background. Applied at once when the player is alive on that team and
	/// commands are allowed (the menu waits for the next spawn); the default agent comes back at the next spawn.
	/// </summary>
	private bool LannSetAgent(CCSPlayerController player, int team, string model)
	{
		try
		{
			if (!Utility.IsPlayerValid(player) || WeaponSync == null || !Config.Additional.AgentEnabled || team is not (2 or 3) ||
			    model is null || !LannCooldownPassed(LannModelCooldown, player.Slot, LannModelCooldownSeconds))
			{
				return false;
			}

			string wanted = model.Length == 0 ? "null" : model;
			var agent = AgentsList.FirstOrDefault(a =>
				a["model"]?.ToString() == wanted && a["team"] != null && (int)a["team"]! == team);
			if (agent == null)
			{
				return false;
			}

			string? stored = wanted == "null" ? null : wanted;
			GPlayersAgent.AddOrUpdate(player.Slot,
				_ => team == 3 ? (stored, null) : (null, stored),
				(_, old) => team == 3 ? (stored, old.T) : (old.CT, stored));

			if (stored != null && _gBCommandsAllowed && player.TeamNum == team &&
			    (LifeState_t)player.LifeState == LifeState_t.LIFE_ALIVE)
			{
				GivePlayerAgent(player);
			}

			PlayerInfo playerInfo = LannPlayerInfo(player);
			_ = Task.Run(async () =>
			{
				try
				{
					await WeaponSync.SyncAgentToDatabase(playerInfo);
				}
				catch (Exception ex)
				{
					Utility.Log($"lann:wp:agent sync failed: {ex.Message}");
				}
			});
			return true;
		}
		catch (Exception ex)
		{
			Logger.LogWarning("lann:wp:agent failed: {Type}", ex.GetType().Name);
			return false;
		}
	}

	/// <summary>
	/// The gloves, as this plugin's gloves menu sets them (SetupGlovesMenu): (<paramref name="gloveDefindex"/>,
	/// <paramref name="paint"/>) must be one of its glove finishes (paint &gt; 0). The glove type and its finish are stored for
	/// the team at once (the menu sets the finish later, inside its database task), the gloves re-given 0.1 and 0.25 s
	/// later, the database synced in the background. Default gloves are not set here (false).
	/// </summary>
	private bool LannSetGloves(CCSPlayerController player, int team, int gloveDefindex, int paint)
	{
		try
		{
			if (!Utility.IsPlayerValid(player) || WeaponSync == null || team is not (0 or 2 or 3) || gloveDefindex <= 0 ||
			    paint <= 0 || !GlovesList.Any(g => ((int?)g["weapon_defindex"] ?? 0) == gloveDefindex && ((int?)g["paint"] ?? 0) == paint) ||
			    !LannCooldownPassed(LannModelCooldown, player.Slot, LannModelCooldownSeconds))
			{
				return false;
			}

			CsTeam[] teams = LannTeams(team);
			var playerGloves = GPlayersGlove.GetOrAdd(player.Slot, _ => new ConcurrentDictionary<CsTeam, ushort>());
			var playerSkins = GPlayerWeaponsInfo.GetOrAdd(player.Slot,
				_ => new ConcurrentDictionary<CsTeam, ConcurrentDictionary<int, WeaponInfo>>());
			foreach (CsTeam side in teams)
			{
				playerGloves[side] = (ushort)gloveDefindex;
				var teamWeapons = playerSkins.GetOrAdd(side, _ => new ConcurrentDictionary<int, WeaponInfo>());
				WeaponInfo info = teamWeapons.GetOrAdd(gloveDefindex, _ => new WeaponInfo());
				info.Paint = paint;
				info.Wear = 0.00f;
				info.Seed = 0;
			}

			PlayerInfo playerInfo = LannPlayerInfo(player);
			_ = Task.Run(async () =>
			{
				try
				{
					await WeaponSync.SyncGloveToDatabase(playerInfo, (ushort)gloveDefindex, teams);
					await WeaponSync.SyncWeaponPaintsToDatabase(playerInfo);
				}
				catch (Exception ex)
				{
					Utility.Log($"lann:wp:gloves sync failed: {ex.Message}");
				}
			});
			// Re-checked before each re-give: the player may have left in between (GivePlayerGloves dereferences the pawn).
			AddTimer(0.1f, () => { if (Utility.IsPlayerValid(player) && player.PlayerPawn.Value != null) GivePlayerGloves(player); });
			AddTimer(0.25f, () => { if (Utility.IsPlayerValid(player) && player.PlayerPawn.Value != null) GivePlayerGloves(player); });
			return true;
		}
		catch (Exception ex)
		{
			Logger.LogWarning("lann:wp:gloves failed: {Type}", ex.GetType().Name);
			return false;
		}
	}

	/// <summary>
	/// Stores <paramref name="paint"/> for <paramref name="defindex"/> in the player's loadout for <paramref name="team"/>.
	/// False (nothing changed) for an invalid player, a disabled skin feature, a team other than 0/2/3, a paint this
	/// plugin's skins list does not have for that weapon, or a second call within a second for the same player. An
	/// existing entry keeps its wear, seed, name tag, StatTrak and stickers; a new one gets this plugin's menu defaults.
	/// </summary>
	private bool LannSetPaint(CCSPlayerController player, int team, int defindex, int paint)
	{
		try
		{
			if (!Utility.IsPlayerValid(player) || WeaponSync == null || !Config.Additional.SkinEnabled ||
			    team is not (0 or 2 or 3) || defindex <= 0 || paint < 0)
			{
				return false;
			}

			if (paint > 0 && !SkinsList.Any(skin =>
				    ((int?)skin["weapon_defindex"] ?? 0) == defindex && ((int?)skin["paint"] ?? 0) == paint))
			{
				return false;
			}

			DateTime now = DateTime.UtcNow;
			if (LannPaintCooldown.TryGetValue(player.Slot, out DateTime until) && now < until)
			{
				return false;
			}

			LannPaintCooldown[player.Slot] = now.AddSeconds(LannPaintCooldownSeconds);

			var playerSkins = GPlayerWeaponsInfo.GetOrAdd(player.Slot,
				_ => new ConcurrentDictionary<CsTeam, ConcurrentDictionary<int, WeaponInfo>>());
			CsTeam[] teams = team == 0 ? [CsTeam.Terrorist, CsTeam.CounterTerrorist] : [(CsTeam)team];
			foreach (CsTeam side in teams)
			{
				var teamWeapons = playerSkins.GetOrAdd(side, _ => new ConcurrentDictionary<int, WeaponInfo>());
				bool existed = teamWeapons.TryGetValue(defindex, out WeaponInfo? info);
				info ??= teamWeapons.GetOrAdd(defindex, _ => new WeaponInfo());
				info.Paint = paint;
				if (!existed || info.Wear <= 0)
				{
					info.Wear = 0.01f;
					info.Seed = 0;
				}
			}

			var playerInfo = new PlayerInfo
			{
				UserId = player.UserId,
				Slot = player.Slot,
				Index = (int)player.Index,
				SteamId = player.SteamID.ToString(),
				Name = player.PlayerName,
				IpAddress = player.IpAddress?.Split(":")[0]
			};

			if (_gBCommandsAllowed && (LifeState_t)player.LifeState == LifeState_t.LIFE_ALIVE)
			{
				RefreshWeapons(player);
			}

			_ = Task.Run(async () =>
			{
				try
				{
					await WeaponSync.SyncWeaponPaintsToDatabase(playerInfo);
				}
				catch (Exception ex)
				{
					Utility.Log($"lann:wp:paint sync failed: {ex.Message}");
				}
			});
			return true;
		}
		catch (Exception ex)
		{
			Logger.LogWarning("lann:wp:paint failed: {Type}", ex.GetType().Name);
			return false;
		}
	}
}
