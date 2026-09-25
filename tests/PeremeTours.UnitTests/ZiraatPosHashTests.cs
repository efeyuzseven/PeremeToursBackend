using PeremeTours.Infrastructure.Payments;

namespace PeremeTours.UnitTests;

public sealed class ZiraatPosHashTests
{
    [Fact]
    public void CreateOrdersAndEscapesFieldsUsingVersionThreeRules()
    {
        var fields = new Dictionary<string, string>
        {
            ["rnd"] = @"a\b|c",
            ["oid"] = "PRM-20260925-ABCDEF1234",
            ["clientid"] = "123456",
            ["amount"] = "1180.00",
            ["encoding"] = "utf-8",
        };

        var result = ZiraatPosHash.Create(fields, "STORE|KEY");

        Assert.Equal(
            "ge4POgjH6+sWN9IyAKct+/MemVtBUIZ702PxOKsTsNcZSNKv8xtv3Mzcj/wJx5ILBpgWSvbJXAEscy40C3lGDQ==",
            result
        );
    }

    [Fact]
    public void VerifyUsesConstantTimeCompatibleHashComparison()
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["amount"] = "1180.00",
            ["clientid"] = "123456",
            ["oid"] = "PRM-20260925-ABCDEF1234",
        };
        fields["HASH"] = ZiraatPosHash.Create(fields, "STORE-KEY");

        Assert.True(ZiraatPosHash.Verify(fields, "STORE-KEY"));

        fields["amount"] = "1180.01";
        Assert.False(ZiraatPosHash.Verify(fields, "STORE-KEY"));
    }

    [Fact]
    public void VerifyRejectsMalformedHash()
    {
        var fields = new Dictionary<string, string>
        {
            ["oid"] = "PRM-20260925-ABCDEF1234",
            ["HASH"] = "not-base64",
        };

        Assert.False(ZiraatPosHash.Verify(fields, "STORE-KEY"));
    }
}
