using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The Photo Box screen's "is the camera even on USB?" verdicts. Three different desks produce
/// the same empty serial-port list — nothing plugged in, a charge-only cable, a serial chip with
/// no driver — and only one of them is fixed by a download. These pin that each desk gets its own
/// sentence and its own next move, because the real one ("Device Descriptor Request Failed" on the
/// owner's desk, 2026-08-20) was first met with a hunt for a driver that could not have helped.
/// </summary>
public class PhotoBoxUsbTests
{
    private static PhotoBoxUsb.Device Dev(string name, string pnpId, uint problem = 0) => new(name, pnpId, problem);

    [Fact]
    public void An_empty_usb_tree_says_plug_it_in()
    {
        var d = PhotoBoxUsb.Classify([]);

        Assert.Equal("none", d.Verdict);
        Assert.Contains("UART/COM", d.WhatToDo);
        Assert.Null(d.DriverUrl);
    }

    [Fact]
    public void A_descriptor_failure_blames_the_cable_and_never_offers_a_driver()
    {
        // The charge-only-cable desk. No driver exists for a device with no identity, so offering
        // one would send the seller to install something that cannot help.
        var d = PhotoBoxUsb.Classify([
            Dev("Unknown USB Device (Device Descriptor Request Failed)", @"USB\VID_0000&PID_0002\6&1785479E&0&3", 43),
        ]);

        Assert.Equal("cable", d.Verdict);
        Assert.Contains("not a missing driver", d.Sentence);
        Assert.Contains("DATA cable", d.WhatToDo);
        Assert.Null(d.DriverUrl);
    }

    [Fact]
    public void A_broken_serial_chip_names_the_chip_and_carries_its_own_driver_link()
    {
        // The missing-driver desk: the CH343 on the Freenove board enumerates, Windows just has
        // nothing to run it with (ConfigManagerErrorCode 28).
        var d = PhotoBoxUsb.Classify([
            Dev("USB2.0-Serial", @"USB\VID_1A86&PID_55D3\5&2C1D&0&2", 28),
        ]);

        Assert.Equal("driver", d.Verdict);
        Assert.Contains("CH343", d.Sentence);
        Assert.Equal(PhotoBoxUsb.WchDriverUrl, d.DriverUrl);
    }

    [Fact]
    public void The_driver_verdict_outranks_the_cable_verdict_when_a_desk_shows_both()
    {
        // Installing the driver is the step that changes what happens next; the descriptor-failed
        // device may even be something else entirely.
        var d = PhotoBoxUsb.Classify([
            Dev("Unknown USB Device (Device Descriptor Request Failed)", @"USB\VID_0000&PID_0002\6&1", 43),
            Dev("USB-Enhanced-SERIAL CH343", @"USB\VID_1A86&PID_55D3\5&2", 28),
        ]);

        Assert.Equal("driver", d.Verdict);
    }

    [Fact]
    public void Espressif_native_usb_gets_a_replug_not_a_download()
    {
        // Windows ships that driver; a download link would send the seller looking for something
        // that does not exist.
        var d = PhotoBoxUsb.Classify([
            Dev("USB JTAG/serial debug unit", @"USB\VID_303A&PID_1001\1", 28),
        ]);

        Assert.Equal("driver", d.Verdict);
        Assert.Null(d.DriverUrl);
        Assert.Contains("replug", d.WhatToDo);
    }

    [Fact]
    public void A_healthy_usb_serial_port_wins_and_names_its_COM()
    {
        var d = PhotoBoxUsb.Classify([
            Dev("Unknown USB Device (Device Descriptor Request Failed)", @"USB\VID_0000&PID_0002\6&1", 43),
            Dev("USB-Enhanced-SERIAL CH343 (COM7)", @"USB\VID_1A86&PID_55D3\5&2", 0),
        ]);

        Assert.Equal("ok", d.Verdict);
        Assert.Contains("COM7", d.Sentence);
        Assert.Contains("Find camera", d.WhatToDo);
    }

    [Fact]
    public void The_ui_asks_this_before_any_port_is_opened()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister.slnx")))
            dir = dir.Parent;
        Assert.True(dir is not null, "could not find the repository root");
        var js = File.ReadAllText(Path.Combine(dir!.FullName, "ING eBay AutoLister", "wwwroot", "app.js"));
        var html = File.ReadAllText(Path.Combine(dir.FullName, "ING eBay AutoLister", "wwwroot", "index.html"));
        var program = File.ReadAllText(Path.Combine(dir.FullName, "ING eBay AutoLister", "Program.cs"));

        Assert.Contains("'/api/photobox/usb'", js);
        Assert.Contains("\"/api/photobox/usb\"", program);
        Assert.Contains("id=\"pb-usb-check\"", html);
        Assert.Contains("id=\"pb-usb-driver\"", html);
        // Opening the screen answers the question by itself, and a healthy port flows straight
        // into the port scan — the seller reads a verdict, not a row of unpressed buttons.
        Assert.Contains("pbCheckUsb();", js);
        Assert.Contains("if (d.verdict === 'ok') pbScanPorts();", js);
    }
}
