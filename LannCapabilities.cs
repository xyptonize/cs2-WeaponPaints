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
/// </summary>
public partial class WeaponPaints
{
	private const string LannPaintCapabilityName = "lann:wp:paint";
	private const double LannPaintCooldownSeconds = 1.0;

	private static readonly PluginCapability<Func<CCSPlayerController, int, int, int, bool>> LannPaintCapability =
		new(LannPaintCapabilityName);

	private static readonly ConcurrentDictionary<int, DateTime> LannPaintCooldown = new();

	private void RegisterLannCapabilities()
	{
		Capabilities.RegisterPluginCapability(LannPaintCapability, () => LannSetPaint);
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
