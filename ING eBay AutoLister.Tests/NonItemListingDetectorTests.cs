using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The screen that keeps a repair shop's $1 service ad off the top of a profit ranking.
///
/// Both halves matter equally and pull against each other, which is why they are tested together:
/// the junk has to go, and the physical goods whose titles contain the same words — repair kits,
/// repair parts, broken units sold for parts — have to stay. A screen that quietly ate real
/// inventory would be a worse bug than the one it fixes, and an invisible one.
/// </summary>
public class NonItemListingDetectorTests
{
    // The listing that started this: a real part number, a $1 ask, and nothing for sale but labour.
    [Theory]
    [InlineData("Fanuc A02B-0168-C013 Repair Evaluation")]
    [InlineData("FANUC A06B-6050-H003 REPAIR EVALUATION")]
    [InlineData("Fanuc A20B-1000-0560 Repair/Evaluation")]
    [InlineData("Allen Bradley 1756-L61 Repair Service")]
    [InlineData("Siemens 6ES7 Flat Rate Repair")]
    [InlineData("We Repair Fanuc A06B Servo Drives")]
    [InlineData("Fanuc Drive - Advance Exchange")]
    [InlineData("Yaskawa Servo Calibration Service")]
    public void ScreensOutServiceListings(string title) =>
        Assert.True(NonItemListingDetector.IsNotTheItem(title));

    [Theory]
    [InlineData("Fanuc A02B-0168-C013 Service Manual")]
    [InlineData("Haas VF-2 Operators Manual")]
    [InlineData("Fanuc 16i/18i Maintenance Manual")]
    [InlineData("Bridgeport Mill Owner's Manual")]
    [InlineData("Hardinge Programmers manual for GT27SP with Fanuc 18T")]
    [InlineData("Fanuc 6M Operation Manual B-54815E")]
    public void ScreensOutPaperwork(string title) =>
        Assert.True(NonItemListingDetector.IsNotTheItem(title));

    [Theory]
    [InlineData("Fanuc A06B-6079-H206 Drive - $200 Core Charge")]
    [InlineData("Rebuilt Spindle Motor (Core Deposit Required)")]
    public void ScreensOutCoreCharges(string title) =>
        Assert.True(NonItemListingDetector.IsNotTheItem(title));

    /// <summary>
    /// The half that protects the board. Every one of these is a physical thing a seller can buy,
    /// resell, and make money on — and every one contains a word from the vocabulary above.
    /// </summary>
    [Theory]
    [InlineData("Fanuc A02B-0168-C013 Control Board")]         // the actual part
    [InlineData("Hydraulic Cylinder Repair Kit - Seals")]      // a kit is goods
    [InlineData("Fanuc Servo Motor - Repair Parts Lot")]       // parts are goods
    [InlineData("Fanuc A06B Drive - For Parts Or Repair")]     // a real broken unit, sold cheap
    [InlineData("Fanuc A06B-6050-H003 - As-Is, Untested")]     // ditto: a genuine sourcing play
    [InlineData("Manual Pallet Jack 5500 lb")]                 // "manual" as an adjective
    [InlineData("Manual Lathe Chuck 8 inch")]
    [InlineData("Core Drill Bit Set - Diamond")]               // "core" as an adjective
    [InlineData("Hardcore Charger 65W USB-C")]                 // the padding case: not "core charge"
    public void KeepsRealGoods(string title) =>
        Assert.False(NonItemListingDetector.IsNotTheItem(title));

    [Fact]
    public void GivesTheReasonItScreened()
    {
        Assert.Equal("a repair service, not the part",
            NonItemListingDetector.Detect("Fanuc A02B-0168-C013 Repair Evaluation"));
        Assert.Equal("a manual, not the item it documents",
            NonItemListingDetector.Detect("Fanuc 16i Service Manual"));
    }

    /// <summary>
    /// A blank or unreadable title is not a screened listing. It is refused further down the
    /// pipeline for having no price or no identity — saying "this is a repair service" about it
    /// would be inventing a reason.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("- - -")]
    public void SaysNothingAboutAnUnreadableTitle(string? title) =>
        Assert.Null(NonItemListingDetector.Detect(title));
}
