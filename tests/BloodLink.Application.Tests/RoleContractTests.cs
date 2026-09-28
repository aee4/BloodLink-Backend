using BloodLink.Application.Contracts;
using BloodLink.Application.DTOs;

namespace BloodLink.Application.Tests;

public class RoleContractTests
{
    [Fact]
    public void RoleNames_AreExplicitAndUnambiguous()
    {
        Assert.Equal("SystemAdmin", RoleNames.SystemAdmin);
        Assert.Equal("FacilityAdmin", RoleNames.FacilityAdmin);
        Assert.Equal("FacilityStaff", RoleNames.FacilityStaff);
    }

    [Fact]
    public void PageRequest_ClampsInputsAndPreventsOffsetOverflow()
    {
        var page = new PageRequest(int.MaxValue, int.MaxValue);
        var first = new PageRequest(0, -10);

        Assert.Equal(int.MaxValue, page.SafeNumber);
        Assert.Equal(100, page.SafeSize);
        Assert.Equal(int.MaxValue, page.SafeOffset);
        Assert.Equal(1, first.SafeNumber);
        Assert.Equal(1, first.SafeSize);
        Assert.Equal(0, first.SafeOffset);
    }
}
