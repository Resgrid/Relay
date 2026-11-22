using System;
using System.Collections.Generic;
using System.Linq;
using Resgrid.Audio.Voice.Dsp;

namespace Resgrid.Relay.Engine.Configuration
{
	/// <summary>
	/// A starting-point tuning preset for the kind of radio the bridge is cabled to. Applying a profile writes its values into
	/// <see cref="RadioModeOptions"/> (so the saved settings are explicit and can be fine-tuned afterwards); nothing is applied
	/// implicitly at run time. The device, PTT and serial/CM108 wiring settings are never touched — they depend on the cable,
	/// not the radio.
	/// </summary>
	public sealed class RadioProfile
	{
		public string Key { get; init; }
		public string DisplayName { get; init; }
		public string Description { get; init; }

		/// <summary>Wiring and first-test advice shown next to the profile in the UI and CLI.</summary>
		public string SetupNotes { get; init; }

		/// <summary>
		/// Push-to-talk-over-cellular (LTE) radio. These need a hard PTT line (the radio must request the floor from its server
		/// before it passes mic audio) and a key-up lead so the first words aren't clipped.
		/// </summary>
		public bool IsPushToTalkOverCellular { get; init; }

		/// <summary>False for the "custom" entry, which leaves every setting as it is.</summary>
		public bool AppliesSettings { get; init; } = true;

		public int TxLeadMs { get; init; }
		public int TxHangMs { get; init; } = 300;
		public int TxTailMs { get; init; } = 150;
		public bool CourtesyTone { get; init; } = true;
		public bool AntiLoop { get; init; } = true;
		public int HighPassCutoffHz { get; init; } = 250;
		public SquelchMode SquelchMode { get; init; } = SquelchMode.Vox;
		public double SquelchOpenDbfs { get; init; } = -38;
		public double SquelchCloseDbfs { get; init; } = -45;
		public int SquelchHangMs { get; init; } = 600;
		public bool DetectMdc1200 { get; init; }
		public bool DetectTones { get; init; }

		/// <summary>Writes this profile's tuning into <paramref name="options"/> and records the profile key.</summary>
		public void ApplyTo(RadioModeOptions options)
		{
			if (options == null)
				throw new ArgumentNullException(nameof(options));

			options.Profile = Key;
			if (!AppliesSettings)
				return;

			options.TxLeadMs = TxLeadMs;
			options.TxHangMs = TxHangMs;
			options.TxTailMs = TxTailMs;
			options.CourtesyTone = CourtesyTone;
			options.AntiLoop = AntiLoop;
			options.HighPassCutoffHz = HighPassCutoffHz;

			options.Squelch ??= new SquelchSettings();
			options.Squelch.Mode = SquelchMode;
			options.Squelch.OpenDbfs = SquelchOpenDbfs;
			options.Squelch.CloseDbfs = SquelchCloseDbfs;
			options.Squelch.HangMs = SquelchHangMs;

			options.Emergency ??= new EmergencyOptions();
			options.Emergency.DetectMdc1200 = DetectMdc1200;
			options.Emergency.DetectTones = DetectTones;
		}

		public override string ToString() => DisplayName;
	}

	/// <summary>The built-in <see cref="RadioProfile"/> catalog.</summary>
	public static class RadioProfiles
	{
		public const string CustomKey = "custom";
		public const string AnalogKey = "analog";
		public const string RapidRadiosKey = "rapidradios";
		public const string DigaTalkPlusKey = "digatalkplus";
		public const string GenericPocKey = "poc";

		// Cellular PTT values are starting points: the floor-grant delay depends on the vendor's server and the LTE link, so
		// the UI's transmit test (and a real over-the-air check) decides the final TxLeadMs. The longer TX hang keeps the floor
		// through normal pauses — every re-key costs another floor request and another lead. Their speakers are silent between
		// transmissions, so the audio-level squelch can sit low, with a longer hang to ride out LTE jitter gaps.
		private const string PocWiring =
			"Wiring: PC sound-card output (attenuated to mic level) to the radio's mic input, the radio's speaker or line output " +
			"to the PC sound-card input, and PTT from a CM108 USB interface GPIO or a serial RTS/DTR line. Use a hard PTT line, " +
			"not VOX: the radio has to request the floor before it passes audio. Turn the radio's own VOX, key-up/roger beeps " +
			"and Bluetooth off, set its volume to a fixed level, and keep it on external 12 V power. Then use Test transmit and " +
			"raise TxLeadMs until a listener on the other side hears the whole test tone.";

		public static IReadOnlyList<RadioProfile> All { get; } = new[]
		{
			new RadioProfile
			{
				Key = CustomKey,
				DisplayName = "Custom",
				Description = "Keep the current settings as they are.",
				SetupNotes = "No preset applied; every value below is used as entered.",
				AppliesSettings = false
			},
			new RadioProfile
			{
				Key = AnalogKey,
				DisplayName = "Analog / LMR radio",
				Description = "Conventional VHF/UHF FM radio through a radio interface (URI, RA-40, RIM-Lite, SignaLink).",
				SetupNotes = "Wiring: interface audio out to the radio mic/data input, radio speaker or discriminator audio to the " +
					"interface input, PTT on the interface's GPIO or serial line. Use carrier (COR/COS) squelch when the interface " +
					"exposes it; otherwise tune the audio squelch with Tune. A short key-up lead lets CTCSS receivers open before speech.",
				TxLeadMs = 150,
				TxHangMs = 300,
				TxTailMs = 150,
				CourtesyTone = true,
				HighPassCutoffHz = 250,
				SquelchMode = SquelchMode.Vox,
				SquelchOpenDbfs = -38,
				SquelchCloseDbfs = -45,
				SquelchHangMs = 600
			},
			new RadioProfile
			{
				Key = RapidRadiosKey,
				DisplayName = "Rapid Radios (LTE PoC)",
				Description = "Rapid Radios base/car unit or handheld used as a donor radio on your Rapid Radios account.",
				SetupNotes = "Use a G1 handheld with an AIOC (All-In-One-Cable) on its Kenwood 2-pin (K1) jack: the AIOC is a CM108 " +
					"interface, so set PTT to Cm108 (or SerialDtr) and pick the AIOC as both audio devices and as the CM108 interface " +
					"(listed as [AIOC]). It is not a C-Media chip: picking it sets Radio:Cm108VendorId and Radio:Cm108ProductId to its " +
					"USB ids, 0x1209 and 0x7388; the default vendor id (0x0D8C) does not find it. Without the desktop app, the console " +
					"'devices' command lists it with the values for relay.user.json (4617 and 29576). Keep the G1 on USB-C power. " +
					"Avoid the Rapid Radios Base/Car Unit: its RJ45 jack carries mic and PTT only, with no speaker or line output. " +
					"The donor radio occupies one radio on your account and bridges the talk group it is set to. " + PocWiring,
				IsPushToTalkOverCellular = true,
				TxLeadMs = 700,
				TxHangMs = 1000,
				TxTailMs = 200,
				CourtesyTone = false,
				HighPassCutoffHz = 200,
				SquelchMode = SquelchMode.Vox,
				SquelchOpenDbfs = -42,
				SquelchCloseDbfs = -50,
				SquelchHangMs = 900
			},
			new RadioProfile
			{
				Key = DigaTalkPlusKey,
				DisplayName = "Diga-Talk+ (LTE PoC)",
				Description = "Diga-Talk+ mobile (DTP-8900 / DTP-8051) or portable used as a donor radio on your Diga-Talk+ account.",
				SetupNotes = "Use a DTP-8900 mobile (12 V, hand mic, 3.5 mm external speaker jack and an accessory/development port " +
					"meant for dispatch consoles and RoIP gateways) with a CM108 interface such as a DMK URI: URI pin 1 to radio PTT, " +
					"pin 22 to radio TX audio, pin 21 to radio RX audio (attenuate speaker-level audio), pins 13/19/20 to ground, and " +
					"pin 8 to a COR/busy output if the port has one. Get the accessory-port pinout from Diga-Talk+ first. The DTP-8900 " +
					"covers LTE bands 2/4/5/12/17 (no Verizon band 13 or FirstNet band 14), so check coverage. The donor radio bridges " +
					"the talk group it is set to. " + PocWiring,
				IsPushToTalkOverCellular = true,
				TxLeadMs = 600,
				TxHangMs = 1000,
				TxTailMs = 200,
				CourtesyTone = false,
				HighPassCutoffHz = 200,
				SquelchMode = SquelchMode.Vox,
				SquelchOpenDbfs = -42,
				SquelchCloseDbfs = -50,
				SquelchHangMs = 900
			},
			new RadioProfile
			{
				Key = GenericPocKey,
				DisplayName = "Other LTE / PoC radio",
				Description = "Any push-to-talk-over-cellular radio (Inrico, Retevis, Hytera PoC, Zello radios, carrier PTT handsets).",
				SetupNotes = PocWiring,
				IsPushToTalkOverCellular = true,
				TxLeadMs = 700,
				TxHangMs = 1000,
				TxTailMs = 200,
				CourtesyTone = false,
				HighPassCutoffHz = 200,
				SquelchMode = SquelchMode.Vox,
				SquelchOpenDbfs = -42,
				SquelchCloseDbfs = -50,
				SquelchHangMs = 900
			}
		};

		/// <summary>The profile with <paramref name="key"/> (case-insensitive), or <see cref="Custom"/> when unknown or blank.</summary>
		public static RadioProfile Find(string key)
		{
			if (string.IsNullOrWhiteSpace(key))
				return Custom;

			return All.FirstOrDefault(p => string.Equals(p.Key, key.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Custom;
		}

		public static RadioProfile Custom => All[0];
	}
}
