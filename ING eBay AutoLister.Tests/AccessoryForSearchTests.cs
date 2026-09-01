using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// The owner searched the eBay scanner for "antminer" and the first four rows were a P13-to-C20
// power cord, a PSU cable, another PSU cable and a fan-duct silencer — "why is it sending all
// junk". Nothing was miscalculated: an $18 cable that resells for $89 really is $72 net at 410%
// ROI, and the board ranks on money-against-ROI. The rows just weren't Antminers.
//
// What's pinned here is the line between "an accessory FOR the thing" and "the thing", and the
// half that matters more: that deliberately hunting parts still works, because the test is made
// against the seller's own search words rather than a blocklist.
public class AccessoryForSearchTests
{
    [Fact]
    public void RejectsThePowerCordsThatLedTheBoard()
    {
        Assert.NotNull(JackpotHunter.AccessoryForSearch(
            "Brand New 12AWG P13 To C20 Power Supply Cord Cable For Bitmain Antminer S21", "antminer"));
        Assert.NotNull(JackpotHunter.AccessoryForSearch(
            "Brand New 12AWG PSU S21 Power Supply Cord Cable For Bitmain Antminer S21", "antminer"));
        Assert.NotNull(JackpotHunter.AccessoryForSearch(
            "12AWG S21 Power Supply Cord Cable PSU For Bitmain Antminer S21", "antminer"));
        Assert.NotNull(JackpotHunter.AccessoryForSearch(
            "Dual 120mm to 7 Inch Max Fan Duct Intake Exhaust Silencer ASIC Crypto Antminer", "antminer"));
    }

    [Fact]
    public void KeepsTheMinerItself()
    {
        Assert.Null(JackpotHunter.AccessoryForSearch(
            "Bitmain Antminer S19j Pro 104TH Bitcoin Miner SHA-256 Tested", "antminer"));
        Assert.Null(JackpotHunter.AccessoryForSearch(
            "Antminer S21 200TH/s Bitcoin Miner - Used, Powered On", "antminer s21"));
    }

    // The whole reason the check reads the search term instead of a blocklist. Somebody sourcing
    // PSUs to resell is running a real business, and a filter that answers their search with
    // nothing is the same failure in the other direction.
    [Fact]
    public void KeepsTheAccessoryWhenTheAccessoryIsWhatWasSearchedFor()
    {
        Assert.Null(JackpotHunter.AccessoryForSearch(
            "Brand New 12AWG PSU Power Supply Cord Cable For Bitmain Antminer S21", "antminer psu cable"));
        Assert.Null(JackpotHunter.AccessoryForSearch(
            "Dual 120mm Fan Duct Intake Exhaust Silencer ASIC Crypto Antminer", "antminer fan duct"));
        Assert.Null(JackpotHunter.AccessoryForSearch(
            "Replacement Control Board for Antminer S19", "replacement control board antminer"));
    }

    [Fact]
    public void NamesTheReasonSoTheRowCanExplainItself()
    {
        var reason = JackpotHunter.AccessoryForSearch(
            "12AWG Power Supply Cord Cable For Bitmain Antminer S21", "antminer");
        Assert.NotNull(reason);
        Assert.Contains("antminer", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CatchesFitsForAndCompatibleWording()
    {
        Assert.NotNull(JackpotHunter.AccessoryForSearch("Cooling Fan fit Bitmain Antminer S19", "antminer"));
        Assert.NotNull(JackpotHunter.AccessoryForSearch("Compatible Power Supply for Antminer L7", "antminer"));
        Assert.NotNull(JackpotHunter.AccessoryForSearch("Aftermarket Hashboard Tester Antminer", "antminer"));
    }

    // Firmware unlocks and hosting contracts name the model and cost less than the machine, so they
    // arrive looking like the best flip on the board. There is nothing to ship.
    [Fact]
    public void RejectsServicesDressedAsProducts()
    {
        Assert.NotNull(JackpotHunter.AccessoryForSearch("Antminer S19 Firmware Unlock Service", "antminer"));
        Assert.NotNull(JackpotHunter.AccessoryForSearch("Antminer Hosting 1 Month Rental", "antminer"));
    }

    // A category sweep has no keyword behind it, so it has nothing to call these listings an
    // accessory TO. Silence is the correct answer, not a guess.
    [Fact]
    public void HasNoOpinionWithoutASearchTerm()
    {
        Assert.Null(JackpotHunter.AccessoryForSearch("Power Supply Cord Cable For Bitmain Antminer S21", ""));
        Assert.Null(JackpotHunter.AccessoryForSearch("Power Supply Cord Cable For Bitmain Antminer S21", null));
        Assert.Null(JackpotHunter.AccessoryForSearch("", "antminer"));
    }

    // Works away from mining hardware, which is the point of testing against the search rather
    // than a list of miner parts.
    [Fact]
    public void GeneralisesBeyondMiners()
    {
        Assert.NotNull(JackpotHunter.AccessoryForSearch("Replacement Filters for iRobot Roomba i7", "roomba"));
        Assert.NotNull(JackpotHunter.AccessoryForSearch("Screen Protector for iPhone 15 Pro Max", "iphone 15"));
        Assert.Null(JackpotHunter.AccessoryForSearch("Apple iPhone 15 Pro Max 256GB Unlocked", "iphone 15"));
    }
}
