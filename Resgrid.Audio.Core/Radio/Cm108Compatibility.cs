using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HidSharp;

namespace Resgrid.Audio.Core.Radio
{
	/// <summary>A USB interface family that keys PTT through the CM108 GPIO HID output report.</summary>
	public sealed class Cm108Interface
	{
		public Cm108Interface(int vendorId, int? productId, string name)
		{
			VendorId = vendorId;
			ProductId = productId;
			Name = name;
		}

		public int VendorId { get; }

		/// <summary>The one compatible product, or null when every product of the vendor is (C-Media).</summary>
		public int? ProductId { get; }

		public string Name { get; }

		public bool Matches(int vendorId, int productId) => VendorId == vendorId && (ProductId == null || ProductId == productId);

		public override string ToString() =>
			$"{Cm108Compatibility.FormatIds(VendorId, ProductId ?? 0)} [{Name}]";
	}

	/// <summary>A USB HID device as enumerated: its ids and the name it reports.</summary>
	public sealed class UsbHidDevice
	{
		public int VendorId { get; set; }
		public int ProductId { get; set; }
		public string Name { get; set; }

		/// <summary>The CM108-compatible family this device belongs to, or null.</summary>
		public Cm108Interface Cm108Interface => Cm108Compatibility.FindInterface(VendorId, ProductId);
	}

	/// <summary>
	/// The USB ids of the interfaces Relay can key PTT through with the CM108 GPIO report, and the one rule for which device
	/// the configured <c>Radio:Cm108VendorId</c> / <c>Radio:Cm108ProductId</c> select. The device pickers (desktop
	/// Configuration and Devices screens, console <c>devices</c>) list <see cref="SelectCompatible"/>, and
	/// <see cref="Cm108PttController"/> opens the device <see cref="IsSelectedBy"/> accepts, so a device picked from a list is
	/// the device that keys. When the ids select more than one device it opens none (<see cref="SelectPttDevice"/>).
	/// </summary>
	public static class Cm108Compatibility
	{
		/// <summary>C-Media: CM108, CM108AH, CM108B, CM119 and CM119A sound fobs (DMK URI, RA-40, RIM-Lite, RB-USB).</summary>
		public const int CMediaVendorId = 0x0D8C;

		/// <summary>The pid.codes vendor id the AIOC (All-In-One-Cable) uses. Shared by unrelated open-source hardware.</summary>
		public const int AiocVendorId = 0x1209;

		/// <summary>The AIOC's product id under <see cref="AiocVendorId"/>.</summary>
		public const int AiocProductId = 0x7388;

		public static readonly Cm108Interface CMedia = new Cm108Interface(CMediaVendorId, null, "C-Media CM108/CM119");

		public static readonly Cm108Interface Aioc = new Cm108Interface(AiocVendorId, AiocProductId, "AIOC");

		/// <summary>Every CM108-compatible interface family Relay lists in its device pickers.</summary>
		public static IReadOnlyList<Cm108Interface> KnownInterfaces { get; } = new[] { CMedia, Aioc };

		/// <summary>The CM108-compatible family a device belongs to, or null.</summary>
		public static Cm108Interface FindInterface(int vendorId, int productId) =>
			KnownInterfaces.FirstOrDefault(i => i.Matches(vendorId, productId));

		public static bool IsCompatible(int vendorId, int productId) => FindInterface(vendorId, productId) != null;

		/// <summary>The CM108-compatible devices among <paramref name="devices"/>, in enumeration order.</summary>
		public static IReadOnlyList<UsbHidDevice> SelectCompatible(IEnumerable<UsbHidDevice> devices) =>
			(devices ?? Enumerable.Empty<UsbHidDevice>())
				.Where(d => d != null && IsCompatible(d.VendorId, d.ProductId))
				.ToList();

		/// <summary>
		/// Whether the configured ids select a device. A non-zero product id must match exactly. Product id 0 means any product
		/// of the vendor, except for a vendor Relay knows only some products of: VID 0x1209 is pid.codes, shared by unrelated
		/// open-source devices, so 0x1209 with product id 0 selects only the AIOC.
		/// </summary>
		public static bool IsSelectedBy(int configuredVendorId, int configuredProductId, int vendorId, int productId)
		{
			if (vendorId != configuredVendorId)
				return false;
			if (configuredProductId != 0)
				return productId == configuredProductId;

			var vendorFamilies = KnownInterfaces.Where(i => i.VendorId == vendorId).ToList();
			return vendorFamilies.Count == 0 || vendorFamilies.Any(i => i.Matches(vendorId, productId));
		}

		/// <summary>Every one of <paramref name="devices"/> the configured ids select, in enumeration order.</summary>
		public static IReadOnlyList<UsbHidDevice> FindAllSelected(IEnumerable<UsbHidDevice> devices, int configuredVendorId, int configuredProductId) =>
			(devices ?? Enumerable.Empty<UsbHidDevice>())
				.Where(d => d != null && IsSelectedBy(configuredVendorId, configuredProductId, d.VendorId, d.ProductId))
				.ToList();

		/// <summary>
		/// The one device of <paramref name="devices"/> the configured ids select (the one PTT keys), or null when none does or
		/// when more than one does: PTT refuses an ambiguous match (<see cref="SelectPttDevice"/>).
		/// </summary>
		public static UsbHidDevice FindSelected(IEnumerable<UsbHidDevice> devices, int configuredVendorId, int configuredProductId)
		{
			var selected = FindAllSelected(devices, configuredVendorId, configuredProductId);
			return selected.Count == 1 ? selected[0] : null;
		}

		/// <summary>
		/// The device <see cref="Cm108PttController"/> opens: the one of <paramref name="present"/> the configured ids select.
		/// Throws <see cref="InvalidOperationException"/>, naming the fix, when none does, and when more than one does: Relay
		/// tells CM108 interfaces apart only by USB id, so keying the first of two (two AIOCs) could key a different radio.
		/// </summary>
		public static UsbHidDevice SelectPttDevice(IEnumerable<UsbHidDevice> present, int configuredVendorId, int configuredProductId)
		{
			var devices = (present ?? Enumerable.Empty<UsbHidDevice>()).ToList();
			var selected = FindAllSelected(devices, configuredVendorId, configuredProductId);
			if (selected.Count == 0)
				throw new InvalidOperationException(NoDeviceFoundMessage(configuredVendorId, configuredProductId, devices));
			if (selected.Count > 1)
				throw new InvalidOperationException(AmbiguousDevicesMessage(configuredVendorId, configuredProductId, selected));
			return selected[0];
		}

		/// <summary>How many of <paramref name="devices"/> have the same vendor and product id as <paramref name="device"/> (itself included).</summary>
		public static int CountWithSameIds(IEnumerable<UsbHidDevice> devices, UsbHidDevice device) =>
			device == null ? 0 : (devices ?? Enumerable.Empty<UsbHidDevice>())
				.Count(d => d != null && d.VendorId == device.VendorId && d.ProductId == device.ProductId);

		/// <summary>A device list's note for an interface that shares its ids with others (PTT will not key any of them), or "".</summary>
		public static string SameIdsNote(int count) =>
			count > 1 ? $" ({count} connected with these ids: Cm108 PTT needs only one connected)" : "";

		/// <summary>"VID 0x1209, PID 0x7388", or "VID 0x0D8C, any PID" for product id 0.</summary>
		public static string FormatIds(int vendorId, int productId) =>
			$"VID 0x{vendorId:X4}, {(productId == 0 ? "any PID" : $"PID 0x{productId:X4}")}";

		/// <summary>One line for a device list: "VID=0x1209 PID=0x7388 All-In-One-Cable [AIOC]".</summary>
		public static string Describe(UsbHidDevice device)
		{
			if (device == null)
				return "";
			var family = device.Cm108Interface;
			var name = string.IsNullOrWhiteSpace(device.Name) ? "(unnamed)" : device.Name.Trim();
			return $"VID=0x{device.VendorId:X4} PID=0x{device.ProductId:X4} {name}" + (family == null ? "" : $" [{family.Name}]");
		}

		/// <summary>The settings that select <paramref name="device"/>, as relay.user.json takes them (decimal).</summary>
		public static string SettingsFor(UsbHidDevice device) =>
			device == null ? "" : $"Radio:Cm108VendorId={device.VendorId}, Radio:Cm108ProductId={device.ProductId}";

		/// <summary>"0x1209": how the desktop form shows a USB id.</summary>
		public static string FormatUsbId(int id) => $"0x{id:X4}";

		/// <summary>
		/// Parses a USB id typed as hexadecimal, with or without a 0x prefix ("0x1209", "1209"). False for anything else,
		/// including values above 0xFFFF.
		/// </summary>
		public static bool TryParseUsbId(string text, out int id)
		{
			id = 0;
			var value = text?.Trim() ?? "";
			if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
				value = value.Substring(2);
			if (value.Length == 0 || value.Length > 4)
				return false;
			return int.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out id);
		}

		/// <summary>Why no device was found for the configured ids, naming the compatible interfaces that are connected.</summary>
		public static string NoDeviceFoundMessage(int configuredVendorId, int configuredProductId, IEnumerable<UsbHidDevice> present)
		{
			var message = $"No CM108-class HID device found for Radio:Cm108VendorId/Cm108ProductId ({FormatIds(configuredVendorId, configuredProductId)}).";
			var compatible = SelectCompatible(present);
			if (compatible.Count == 0)
				return message + " Check the radio interface is connected.";

			return message + " Connected CM108-compatible interfaces: " + string.Join("; ", compatible.Select(Describe)) +
				$". Pick one as the CM108 interface (Configuration > Radio), or set {SettingsFor(compatible[0])}.";
		}

		/// <summary>Why PTT will not key any of the devices the configured ids select when more than one matches, and what to do.</summary>
		public static string AmbiguousDevicesMessage(int configuredVendorId, int configuredProductId, IEnumerable<UsbHidDevice> matching)
		{
			var devices = (matching ?? Enumerable.Empty<UsbHidDevice>()).Where(d => d != null).ToList();
			var message = $"{devices.Count} CM108-class HID devices match Radio:Cm108VendorId/Cm108ProductId ({FormatIds(configuredVendorId, configuredProductId)}): " +
				string.Join("; ", devices.Select(Describe)) +
				". Relay tells CM108 interfaces apart only by USB id, so it cannot know which one is wired to this radio and will not key PTT on any of them.";

			if (configuredProductId == 0 && devices.Select(d => d.ProductId).Distinct().Count() > 1)
				message += " Pick the radio's interface as the CM108 interface (Configuration > Radio), or set Radio:Cm108ProductId to its product id.";
			if (devices.GroupBy(d => d.ProductId).Any(g => g.Count() > 1))
				message += " Interfaces with the same ids (such as two AIOCs) cannot be told apart: connect only the one wired to this radio, " +
					"or key PTT through a serial line instead (PttMethod SerialRts or SerialDtr; an AIOC keys with SerialDtr on its own COM port).";
			return message;
		}

		/// <summary>Every USB HID device on this machine; empty when the HID backend is unavailable.</summary>
		public static IReadOnlyList<UsbHidDevice> EnumerateHidDevices()
		{
			try
			{
				return DeviceList.Local.GetHidDevices().Select(ToUsbHidDevice).ToList();
			}
			catch
			{
				return Array.Empty<UsbHidDevice>();
			}
		}

		/// <summary>The CM108-compatible USB HID devices on this machine.</summary>
		public static IReadOnlyList<UsbHidDevice> EnumerateCompatibleDevices() => SelectCompatible(EnumerateHidDevices());

		internal static UsbHidDevice ToUsbHidDevice(HidDevice device) =>
			new UsbHidDevice { VendorId = device.VendorID, ProductId = device.ProductID, Name = SafeName(device) };

		internal static string SafeName(HidDevice device)
		{
			try { return device.GetFriendlyName(); }
			catch { return "(unnamed)"; }
		}
	}
}
