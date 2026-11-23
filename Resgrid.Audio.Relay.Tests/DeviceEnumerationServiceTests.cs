using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using NUnit.Framework;
using Resgrid.Audio.Core.Radio;
using Resgrid.Audio.Relay.Services;

namespace Resgrid.Audio.Relay.Tests
{
	/// <summary>
	/// The CM108 interface list behind the desktop Devices screen and the Configuration CM108 picker: C-Media fobs and the
	/// AIOC are listed, other HID devices are not.
	/// </summary>
	[TestFixture]
	public class DeviceEnumerationServiceTests
	{
		private static UsbHidDevice Hid(int vendorId, int productId, string name) =>
			new UsbHidDevice { VendorId = vendorId, ProductId = productId, Name = name };

		[Test]
		public void Cm108_Devices_Include_The_Aioc_And_C_Media_Fobs_Only()
		{
			var service = new DeviceEnumerationService(() => new[]
			{
				Hid(0x046D, 0xC52B, "Logitech receiver"),
				Hid(0x1209, 0x7388, "All-In-One-Cable"),
				Hid(0x1209, 0x0001, "Open-source keyboard"),
				Hid(0x0D8C, 0x013C, "USB Audio Device")
			});

			var devices = service.GetCm108Devices();

			devices.Select(d => (d.VendorId, d.ProductId)).Should().Equal((0x1209, 0x7388), (0x0D8C, 0x013C));
			devices[0].InterfaceName.Should().Be("AIOC");
			devices[0].Display.Should().Be("VID=0x1209 PID=0x7388 All-In-One-Cable [AIOC]");
			devices[1].InterfaceName.Should().Be("C-Media CM108/CM119");
			devices[1].Display.Should().Be("VID=0x0D8C PID=0x013C USB Audio Device [C-Media CM108/CM119]");
		}

		[Test]
		public void Identical_Interfaces_Are_Both_Listed_And_Flagged()
		{
			// Two AIOCs: picking either writes the same ids, so the picker cannot say which; PTT refuses both, and the list says so.
			var service = new DeviceEnumerationService(() => new[]
			{
				Hid(0x1209, 0x7388, "All-In-One-Cable"),
				Hid(0x0D8C, 0x013C, "USB Audio Device"),
				Hid(0x1209, 0x7388, "All-In-One-Cable")
			});

			var devices = service.GetCm108Devices();

			devices.Select(d => d.SameIdsCount).Should().Equal(2, 1, 2);
			devices[0].Display.Should().StartWith("VID=0x1209 PID=0x7388 All-In-One-Cable [AIOC] (2 connected with these ids");
			devices[1].Display.Should().Be("VID=0x0D8C PID=0x013C USB Audio Device [C-Media CM108/CM119]");
		}

		[Test]
		public void A_Failing_Hid_Backend_Yields_An_Empty_List()
		{
			var service = new DeviceEnumerationService(() => throw new InvalidOperationException("no HID backend"));

			service.GetCm108Devices().Should().BeEmpty();
		}

		[Test]
		public void No_Hid_Devices_Yields_An_Empty_List()
		{
			new DeviceEnumerationService(() => Array.Empty<UsbHidDevice>()).GetCm108Devices().Should().BeEmpty();
			new DeviceEnumerationService(() => (IEnumerable<UsbHidDevice>)null).GetCm108Devices().Should().BeEmpty();
		}
	}
}
