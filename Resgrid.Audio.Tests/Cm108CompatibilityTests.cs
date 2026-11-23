using System.Linq;
using FluentAssertions;
using NUnit.Framework;
using Resgrid.Audio.Core.Radio;

namespace Resgrid.Audio.Tests
{
	/// <summary>
	/// Which USB HID devices Relay lists as CM108-compatible PTT interfaces, and which one the configured
	/// Radio:Cm108VendorId / Radio:Cm108ProductId make <see cref="Cm108PttController"/> open.
	/// </summary>
	[TestFixture]
	public class Cm108CompatibilityTests
	{
		private static UsbHidDevice Hid(int vendorId, int productId, string name = "device") =>
			new UsbHidDevice { VendorId = vendorId, ProductId = productId, Name = name };

		private static readonly UsbHidDevice CMediaFob = Hid(0x0D8C, 0x013C, "USB Audio Device");
		private static readonly UsbHidDevice CMediaCm119 = Hid(0x0D8C, 0x0008, "C-Media CM119");
		private static readonly UsbHidDevice Aioc = Hid(0x1209, 0x7388, "All-In-One-Cable");
		private static readonly UsbHidDevice OtherPidCodesDevice = Hid(0x1209, 0x0001, "Some open-source keyboard");
		private static readonly UsbHidDevice Mouse = Hid(0x046D, 0xC52B, "Logitech receiver");

		[TestCase(0x0D8C, 0x000C, true)]
		[TestCase(0x0D8C, 0x013C, true)]
		[TestCase(0x0D8C, 0x0012, true)]
		[TestCase(0x1209, 0x7388, true)]
		[TestCase(0x1209, 0x0001, false)]
		[TestCase(0x1209, 0x0000, false)]
		[TestCase(0x046D, 0xC52B, false)]
		[TestCase(0x7388, 0x1209, false)]
		public void Lists_C_Media_Fobs_And_The_Aioc_Only(int vendorId, int productId, bool compatible)
		{
			Cm108Compatibility.IsCompatible(vendorId, productId).Should().Be(compatible);
		}

		[Test]
		public void The_Aioc_Is_Named_In_Device_Lists()
		{
			Cm108Compatibility.FindInterface(0x1209, 0x7388).Should().BeSameAs(Cm108Compatibility.Aioc);
			Cm108Compatibility.FindInterface(0x0D8C, 0x013C).Should().BeSameAs(Cm108Compatibility.CMedia);
			Cm108Compatibility.FindInterface(0x1209, 0x0001).Should().BeNull();

			Cm108Compatibility.Describe(Aioc).Should().Be("VID=0x1209 PID=0x7388 All-In-One-Cable [AIOC]");
			Cm108Compatibility.Describe(Mouse).Should().Be("VID=0x046D PID=0xC52B Logitech receiver");
			Cm108Compatibility.Describe(Hid(0x0D8C, 0x000C, " ")).Should().Be("VID=0x0D8C PID=0x000C (unnamed) [C-Media CM108/CM119]");
		}

		[Test]
		public void SelectCompatible_Keeps_The_Interfaces_In_Enumeration_Order()
		{
			var all = new[] { Mouse, Aioc, OtherPidCodesDevice, CMediaFob, null, CMediaCm119 };

			Cm108Compatibility.SelectCompatible(all).Should().Equal(Aioc, CMediaFob, CMediaCm119);
			Cm108Compatibility.SelectCompatible(null).Should().BeEmpty();
		}

		[Test]
		public void Default_Settings_Select_A_C_Media_Fob_But_Not_The_Aioc()
		{
			// Radio:Cm108VendorId=0x0D8C, Radio:Cm108ProductId=0 (the defaults).
			Cm108Compatibility.IsSelectedBy(0x0D8C, 0, CMediaFob.VendorId, CMediaFob.ProductId).Should().BeTrue();
			Cm108Compatibility.IsSelectedBy(0x0D8C, 0, CMediaCm119.VendorId, CMediaCm119.ProductId).Should().BeTrue();
			Cm108Compatibility.IsSelectedBy(0x0D8C, 0, Aioc.VendorId, Aioc.ProductId).Should().BeFalse();

			Cm108Compatibility.FindSelected(new[] { Mouse, Aioc, CMediaFob }, 0x0D8C, 0).Should().BeSameAs(CMediaFob);
			Cm108Compatibility.FindSelected(new[] { Mouse, Aioc }, 0x0D8C, 0).Should().BeNull();
		}

		// ---- more than one device matches: PTT opens none of them ----------------------------------------------------------

		[Test]
		public void Two_Identical_Interfaces_Are_Refused_Naming_The_Fix()
		{
			var first = Hid(0x1209, 0x7388, "All-In-One-Cable");
			var second = Hid(0x1209, 0x7388, "All-In-One-Cable");
			var present = new[] { Mouse, first, second };

			Cm108Compatibility.FindAllSelected(present, 0x1209, 0x7388).Should().Equal(first, second);
			Cm108Compatibility.FindSelected(present, 0x1209, 0x7388).Should().BeNull("PTT would key neither");

			var refuse = () => Cm108Compatibility.SelectPttDevice(present, 0x1209, 0x7388);
			refuse.Should().Throw<System.InvalidOperationException>()
				.WithMessage("2 CM108-class HID devices match*VID 0x1209, PID 0x7388*will not key PTT on any of them*connect only the one wired to this radio*SerialDtr*")
				.Which.Message.Should().NotContain("Cm108ProductId to its product id", "the product id cannot tell identical interfaces apart");
		}

		[Test]
		public void Any_Product_Matching_Two_Different_Fobs_Is_Refused_And_Asks_For_The_Product_Id()
		{
			var refuse = () => Cm108Compatibility.SelectPttDevice(new[] { CMediaFob, Aioc, CMediaCm119 }, 0x0D8C, 0);

			refuse.Should().Throw<System.InvalidOperationException>()
				.WithMessage("2 CM108-class HID devices match*VID 0x0D8C, any PID*USB Audio Device*C-Media CM119*set Radio:Cm108ProductId*")
				.Which.Message.Should().NotContain("such as two AIOCs");

			// The ids a picker writes for either of them select only that one.
			Cm108Compatibility.SelectPttDevice(new[] { CMediaFob, Aioc, CMediaCm119 }, 0x0D8C, 0x0008).Should().BeSameAs(CMediaCm119);
		}

		[Test]
		public void A_Single_Match_Is_Opened_And_No_Match_Says_So()
		{
			Cm108Compatibility.SelectPttDevice(new[] { Mouse, CMediaFob, Aioc }, 0x1209, 0x7388).Should().BeSameAs(Aioc);

			var none = () => Cm108Compatibility.SelectPttDevice(new[] { Mouse }, 0x1209, 0x7388);
			none.Should().Throw<System.InvalidOperationException>().WithMessage("No CM108-class HID device found*");
		}

		[Test]
		public void Device_Lists_Flag_Interfaces_That_Share_Their_Ids()
		{
			var first = Hid(0x1209, 0x7388, "All-In-One-Cable");
			var second = Hid(0x1209, 0x7388, "All-In-One-Cable");
			var listed = new[] { first, CMediaFob, second };

			Cm108Compatibility.CountWithSameIds(listed, first).Should().Be(2);
			Cm108Compatibility.CountWithSameIds(listed, CMediaFob).Should().Be(1);
			Cm108Compatibility.SameIdsNote(1).Should().BeEmpty();
			Cm108Compatibility.SameIdsNote(2).Should().Contain("2 connected with these ids").And.Contain("only one");
		}

		[Test]
		public void Aioc_Ids_Select_The_Aioc()
		{
			Cm108Compatibility.FindSelected(new[] { CMediaFob, OtherPidCodesDevice, Aioc }, 0x1209, 0x7388).Should().BeSameAs(Aioc);
		}

		[Test]
		public void Pid_Codes_Vendor_With_Any_Product_Selects_Only_The_Aioc()
		{
			// 0x1209 is shared by unrelated open-source hardware: "any product" must not key some other device's HID reports.
			Cm108Compatibility.IsSelectedBy(0x1209, 0, OtherPidCodesDevice.VendorId, OtherPidCodesDevice.ProductId).Should().BeFalse();
			Cm108Compatibility.FindSelected(new[] { OtherPidCodesDevice, Aioc }, 0x1209, 0).Should().BeSameAs(Aioc);
		}

		[Test]
		public void An_Explicit_Product_Id_Must_Match_Exactly()
		{
			Cm108Compatibility.IsSelectedBy(0x0D8C, 0x0008, CMediaCm119.VendorId, CMediaCm119.ProductId).Should().BeTrue();
			Cm108Compatibility.IsSelectedBy(0x0D8C, 0x0008, CMediaFob.VendorId, CMediaFob.ProductId).Should().BeFalse();
			// An operator override outside the known list still selects its device.
			Cm108Compatibility.IsSelectedBy(0x1209, 0x0001, OtherPidCodesDevice.VendorId, OtherPidCodesDevice.ProductId).Should().BeTrue();
		}

		[Test]
		public void An_Unknown_Vendor_With_Any_Product_Selects_Every_Product_Of_That_Vendor()
		{
			Cm108Compatibility.IsSelectedBy(0x046D, 0, Mouse.VendorId, Mouse.ProductId).Should().BeTrue();
			Cm108Compatibility.IsSelectedBy(0x046D, 0, Aioc.VendorId, Aioc.ProductId).Should().BeFalse();
		}

		[Test]
		public void Every_Listed_Interface_Is_Keyed_By_The_Ids_A_Picker_Writes()
		{
			// Picking a device writes its own vendor and product id: the PTT controller must then open that device.
			var listed = Cm108Compatibility.SelectCompatible(new[] { Mouse, Aioc, OtherPidCodesDevice, CMediaFob, CMediaCm119 });

			foreach (var device in listed)
				Cm108Compatibility.FindSelected(listed, device.VendorId, device.ProductId).Should().BeSameAs(device);
		}

		[TestCase("0x1209", 0x1209)]
		[TestCase("0X7388", 0x7388)]
		[TestCase(" 0d8c ", 0x0D8C)]
		[TestCase("0", 0)]
		[TestCase("0x0000", 0)]
		[TestCase("FFFF", 0xFFFF)]
		public void Usb_Ids_Parse_As_Hex(string text, int expected)
		{
			Cm108Compatibility.TryParseUsbId(text, out var id).Should().BeTrue();
			id.Should().Be(expected);
		}

		[TestCase(null)]
		[TestCase("")]
		[TestCase("0x")]
		[TestCase("0x10000")]
		[TestCase("12g4")]
		[TestCase("-1")]
		public void Malformed_Usb_Ids_Are_Rejected(string text)
		{
			Cm108Compatibility.TryParseUsbId(text, out _).Should().BeFalse();
		}

		[Test]
		public void Usb_Ids_Round_Trip_Through_The_Form()
		{
			Cm108Compatibility.FormatUsbId(0x1209).Should().Be("0x1209");
			Cm108Compatibility.FormatUsbId(0).Should().Be("0x0000");
			Cm108Compatibility.TryParseUsbId(Cm108Compatibility.FormatUsbId(0x0D8C), out var id).Should().BeTrue();
			id.Should().Be(0x0D8C);
		}

		[Test]
		public void No_Device_Message_Points_At_A_Connected_Aioc()
		{
			var message = Cm108Compatibility.NoDeviceFoundMessage(0x0D8C, 0, new[] { Mouse, Aioc });

			message.Should().Contain("VID 0x0D8C, any PID")
				.And.Contain("VID=0x1209 PID=0x7388 All-In-One-Cable [AIOC]")
				.And.Contain("Radio:Cm108VendorId=4617, Radio:Cm108ProductId=29576");
		}

		[Test]
		public void No_Device_Message_Without_Any_Interface_Asks_To_Connect_One()
		{
			var message = Cm108Compatibility.NoDeviceFoundMessage(0x1209, 0x7388, new[] { Mouse });

			message.Should().Contain("VID 0x1209, PID 0x7388").And.Contain("Check the radio interface is connected");
			message.Should().NotContain("Logitech");
		}

		[Test]
		public void Known_Interfaces_Describe_Their_Ids()
		{
			Cm108Compatibility.KnownInterfaces.Select(i => i.ToString()).Should().Equal(
				"VID 0x0D8C, any PID [C-Media CM108/CM119]",
				"VID 0x1209, PID 0x7388 [AIOC]");
		}
	}
}
