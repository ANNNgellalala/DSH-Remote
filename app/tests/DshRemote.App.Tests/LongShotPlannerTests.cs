using System;
using System.Linq;
using DshRemote.App.Services;
using Xunit;

namespace DshRemote.App.Tests;

/// <summary>
/// Long screenshot sizing/splitting/naming rules. These are the safety net for requirement 3: a long
/// session must never produce an image over the pixel/edge budget, must never lose content when it is
/// split, and must keep a readable width.
/// </summary>
public sealed class LongShotPlannerTests
{
    [Fact]
    public void NormalPhoneSession_FitsInOneImage()
    {
        // 360dp wide at 2.75 density = 990 px; 2 000dp of transcript = 5 500 px → 5.4 M px.
        var plan = LongShotPlanner.Create(360, 2000, 2.75);

        Assert.Equal(990, plan.PixelWidth);
        Assert.Equal(5500, plan.TotalPixelHeight);
        Assert.Single(plan.Segments);
        Assert.False(plan.Downscaled);
        Assert.False(plan.ExceededBudget);
        Assert.Equal(5500, plan.Segments[0].PixelHeight);
        Assert.Equal(0, plan.Segments[0].OffsetY);
        Assert.Equal(2.75, plan.RenderScale, 3);
    }

    [Fact]
    public void TotalPixelBudget_IsEnforcedByDownscaling()
    {
        // 800 × 40 000 px = 32 M px, over the 24 M total budget: width must shrink, ratio preserved.
        var plan = LongShotPlanner.Create(400, 20000, 2.0);

        Assert.True(plan.Downscaled);
        Assert.True(plan.TotalPixels <= 24_000_000 + plan.PixelWidth);
        Assert.InRange(plan.PixelWidth, 640, 800);
        AssertCoverage(plan);
    }

    [Fact]
    public void SingleImageNeverExceedsEdgeOrPixelBudget()
    {
        // 1080 px wide, 12 000 px tall would be 12.96 M px → must split even though only one edge is over.
        var plan = LongShotPlanner.Create(360, 4000, 3.0);

        Assert.Equal(1080, plan.PixelWidth);
        Assert.Equal(12000, plan.TotalPixelHeight);
        Assert.True(plan.Segments.Count >= 2);
        Assert.All(plan.Segments, segment =>
        {
            Assert.True(segment.PixelHeight <= 12_000);
            Assert.True((long)segment.PixelWidth * segment.PixelHeight <= 8_000_000);
        });
        AssertCoverage(plan);
    }

    [Fact]
    public void VeryLongSession_IsDownscaledBeforeSplittingPastImageBudget()
    {
        // 360dp × 20 000dp at density 3 = 1080 × 60 000 px = 64.8 M px, over the 24 M total budget:
        // the planner has to shrink the width (staying >= 640) and then split into a few images.
        var plan = LongShotPlanner.Create(360, 20000, 3.0);

        Assert.True(plan.Downscaled);
        Assert.False(plan.ExceededBudget);
        Assert.InRange(plan.PixelWidth, 640, 1080);
        Assert.True(plan.TotalPixels <= 24_000_000 + plan.PixelWidth);
        Assert.All(plan.Segments, segment =>
            Assert.True((long)segment.PixelWidth * segment.PixelHeight <= 8_000_000));
        AssertCoverage(plan);
    }

    [Fact]
    public void ExtremelyLongSession_KeepsReadableWidthAndStillCoversEverything()
    {
        const long sessionHeightDip = 120_000;
        var plan = LongShotPlanner.Create(360, sessionHeightDip, 3.0, new LongShotLimits
        {
            MaxImages = 3,
            MaxTotalPixels = 1_000_000,
            MaxPixelsPerImage = 2_000_000,
            MinWidthPixels = 640
        });

        Assert.True(plan.ExceededBudget);
        Assert.Equal(640, plan.PixelWidth);
        // No silent truncation: every pixel of the session is covered by a segment.
        AssertCoverage(plan);
        Assert.All(plan.Segments, segment =>
            Assert.True((long)segment.PixelWidth * segment.PixelHeight <= 2_000_000));
    }

    [Fact]
    public void NarrowContent_IsNotUpscaledToTheMinimumWidth()
    {
        var plan = LongShotPlanner.Create(300, 500, 1.0);

        Assert.Equal(300, plan.PixelWidth);
        Assert.Single(plan.Segments);
        Assert.False(plan.Downscaled);
    }

    [Fact]
    public void VeryWideContent_IsDownscaledToTheEdgeLimit()
    {
        // 5 000dp at density 4 = 20 000 px wide; the total-pixel budget and the 12 000 px edge both
        // force a downscale on both axes while keeping the aspect ratio.
        var plan = LongShotPlanner.Create(5000, 6000, 4.0);

        Assert.True(plan.Downscaled);
        Assert.True(plan.PixelWidth <= 12_000);
        Assert.True(plan.PixelWidth < 20_000);
        Assert.True(plan.TotalPixels <= 24_000_000 + plan.PixelWidth);
        AssertCoverage(plan);
    }

    [Fact]
    public void SegmentsAreContiguousAndNonOverlapping()
    {
        var plan = LongShotPlanner.Create(412, 9000, 2.0);

        var expectedOffset = 0;
        foreach (var segment in plan.Segments)
        {
            Assert.Equal(expectedOffset, segment.OffsetY);
            Assert.True(segment.PixelHeight > 0);
            expectedOffset += segment.PixelHeight;
        }

        Assert.Equal(plan.TotalPixelHeight, expectedOffset);
        Assert.Equal(Enumerable.Range(0, plan.Segments.Count), plan.Segments.Select(segment => segment.Index));
        Assert.Equal(Enumerable.Range(1, plan.Segments.Count), plan.Segments.Select(segment => segment.Number));
    }

    [Fact]
    public void InvalidArguments_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LongShotPlanner.Create(0, 100, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => LongShotPlanner.Create(100, -1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => LongShotPlanner.Create(100, 100, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => LongShotPlanner.Create(double.NaN, 100, 1));
    }

    [Fact]
    public void LimitsAreNormalizedIntoSafeRanges()
    {
        var limits = new LongShotLimits
        {
            MaxTotalPixels = 1,
            MaxPixelsPerImage = 1,
            MaxEdgePixels = 1,
            MinWidthPixels = 1,
            MaxImages = 0
        }.Normalize();

        Assert.Equal(250_000, limits.MaxPixelsPerImage);
        Assert.Equal(1_000, limits.MaxEdgePixels);
        Assert.Equal(200, limits.MinWidthPixels);
        Assert.Equal(1, limits.MaxImages);
        Assert.Equal(1_000_000, limits.MaxTotalPixels);
    }

    [Fact]
    public void ProbeSizedLimits_ForceSegmentationWithoutLosingContent()
    {
        // The desktop probe passes a tiny budget to exercise the multi-image path on short demos.
        var plan = LongShotPlanner.Create(300, 900, 1.0, new LongShotLimits
        {
            MaxTotalPixels = 24_000_000,
            MaxPixelsPerImage = 250_000,
            MaxEdgePixels = 1_000,
            MinWidthPixels = 200,
            MaxImages = 64
        });

        Assert.Equal(300, plan.PixelWidth);
        Assert.True(plan.Segments.Count >= 2);
        Assert.All(plan.Segments, segment =>
        {
            Assert.True(segment.PixelHeight <= 1_000);
            Assert.True((long)segment.PixelWidth * segment.PixelHeight <= 250_000);
        });
        AssertCoverage(plan);
    }

    private static void AssertCoverage(LongShotPlan plan)
    {
        Assert.NotEmpty(plan.Segments);
        Assert.Equal(plan.TotalPixelHeight, plan.Segments.Sum(segment => segment.PixelHeight));
        Assert.Equal(0, plan.Segments[0].OffsetY);
        var last = plan.Segments[^1];
        Assert.Equal(plan.TotalPixelHeight, last.OffsetY + last.PixelHeight);
    }
}

/// <summary>File naming rules for saved long screenshots.</summary>
public sealed class LongShotNamingTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 11, 11, 14, 25, 30, TimeSpan.FromHours(8));

    [Fact]
    public void SingleImage_HasNoPageSuffix()
    {
        var names = LongShotNaming.BuildFileNames("修复登录", Timestamp, 1);

        Assert.Single(names);
        Assert.Equal("dshremote-修复登录-20261111-142530.png", names[0]);
    }

    [Fact]
    public void SplitImages_AreNumberedInOrder()
    {
        var names = LongShotNaming.BuildFileNames("Long chat", Timestamp, 3);

        Assert.Equal(
            new[]
            {
                "dshremote-Long-chat-20261111-142530-p1.png",
                "dshremote-Long-chat-20261111-142530-p2.png",
                "dshremote-Long-chat-20261111-142530-p3.png"
            },
            names);
    }

    [Fact]
    public void Titles_AreSanitizedForFileSystems()
    {
        Assert.Equal("Fix-bug-feature-bad", LongShotNaming.SanitizeTitle("Fix: bug/feature? *bad*"));
        Assert.Equal("会话A", LongShotNaming.SanitizeTitle("  会话A  "));
        Assert.Equal("a-b", LongShotNaming.SanitizeTitle("a\u0000\tb"));
        Assert.Equal("点号", LongShotNaming.SanitizeTitle("..点号.."));
    }

    [Fact]
    public void EmptyOrUnusableTitles_FallBackToDefault()
    {
        Assert.Equal("会话", LongShotNaming.SanitizeTitle(null));
        Assert.Equal("会话", LongShotNaming.SanitizeTitle("   "));
        Assert.Equal("会话", LongShotNaming.SanitizeTitle("///:::"));
    }

    [Fact]
    public void LongTitles_AreTruncated()
    {
        var title = new string('长', 80);
        var sanitized = LongShotNaming.SanitizeTitle(title);

        Assert.True(sanitized.Length <= 32);
        Assert.Equal(32, sanitized.Length);
    }

    [Fact]
    public void CountMustBePositive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LongShotNaming.BuildFileNames("t", Timestamp, 0));
    }

    [Fact]
    public void ResultDescription_MentionsPageCountAndSize()
    {
        Assert.Equal("长图已生成：1080×24000 px", LongShotNaming.DescribeResult("会话", 1, 1080, 24000));
        Assert.Equal("长图已生成：3 张，合计 1080×24000 px", LongShotNaming.DescribeResult("会话", 3, 1080, 24000));
    }
}
