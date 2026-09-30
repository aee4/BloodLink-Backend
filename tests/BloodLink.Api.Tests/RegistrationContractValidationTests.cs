using System.ComponentModel.DataAnnotations;
using BloodLink.Api.Contracts;
using BloodLink.Domain.Enums;

namespace BloodLink.Api.Tests;

public sealed class RegistrationContractValidationTests
{
    [Fact]
    public void Registration_accepts_a_valid_facility_type()
    {
        Assert.Empty(Validate(ValidRegistration()));
    }

    [Fact]
    public void Registration_requires_an_explicit_facility_type()
    {
        var request = ValidRegistration() with { FacilityType = null };

        Assert.Contains(Validate(request), result => result.MemberNames.Contains(nameof(request.FacilityType)));
    }

    [Fact]
    public void Registration_rejects_undefined_facility_types()
    {
        var request = ValidRegistration() with { FacilityType = (FacilityType)999 };

        Assert.Contains(Validate(request), result => result.MemberNames.Contains(nameof(request.FacilityType)));
    }

    private static List<ValidationResult> Validate(object model)
    {
        var results = new List<ValidationResult>();
        var request = (RegisterFacilityBody)model;
        var parameter = typeof(RegisterFacilityBody).GetConstructors().Single().GetParameters()
            .Single(item => item.Name == nameof(request.FacilityType));
        Validator.TryValidateValue(
            request.FacilityType,
            new ValidationContext(request) { MemberName = parameter.Name },
            results,
            parameter.GetCustomAttributes(typeof(ValidationAttribute), inherit: true).Cast<ValidationAttribute>());
        return results;
    }

    private static RegisterFacilityBody ValidRegistration() => new(
        "Test Facility", FacilityType.Hospital, "LIC-100", "Greater Accra", "Accra", "Test Road",
        "facility@example.test", "0240000000", "Test", "Administrator", "admin@example.test",
        "0240000001", "ValidPass1");
}
