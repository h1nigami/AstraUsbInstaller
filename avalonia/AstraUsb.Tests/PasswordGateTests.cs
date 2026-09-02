using AstraUsb.Services;
using Xunit;

namespace AstraUsb.Tests;

/// <summary>
/// Пароль станции. Им закрыт выход из киоска, поэтому проверяется и то, что
/// он пускает, и то, что он не пускает.
/// </summary>
public sealed class PasswordGateTests
{
    [Fact]
    public void Until_a_password_is_set_the_default_one_works()
    {
        Assert.True(PasswordGate.Matches(null, PasswordGate.Fallback));
        Assert.True(PasswordGate.Matches("", PasswordGate.Fallback));
        Assert.False(PasswordGate.Matches(null, "что-то другое"));
    }

    [Fact]
    public void A_set_password_is_accepted_and_a_wrong_one_is_not()
    {
        var stored = PasswordGate.Hash("станция-2026");

        Assert.True(PasswordGate.Matches(stored, "станция-2026"));
        Assert.False(PasswordGate.Matches(stored, "станция-2025"));
        Assert.False(PasswordGate.Matches(stored, ""));
    }

    [Fact]
    public void The_password_itself_is_not_stored()
    {
        var stored = PasswordGate.Hash("станция-2026");

        Assert.DoesNotContain("станция-2026", stored);
    }

    [Fact]
    public void Two_hashes_of_one_password_differ()
    {
        // Соль у каждой записи своя, иначе одинаковые пароли были бы видны
        // по одинаковым хешам.
        Assert.NotEqual(PasswordGate.Hash("одно и то же"), PasswordGate.Hash("одно и то же"));
    }

    [Fact]
    public void A_set_password_replaces_the_default_one()
    {
        var stored = PasswordGate.Hash("станция-2026");

        Assert.False(PasswordGate.Matches(stored, PasswordGate.Fallback));
    }

    [Fact]
    public void The_default_account_works_until_it_is_renamed()
    {
        Assert.True(PasswordGate.AccountMatches(null, "admin"));
        Assert.True(PasswordGate.AccountMatches("", "admin"));
        Assert.False(PasswordGate.AccountMatches(null, "оператор"));
    }

    [Fact]
    public void The_account_name_ignores_case_and_spaces()
    {
        // На сенсорном экране заглавная буква появляется случайно чаще, чем
        // намеренно.
        Assert.True(PasswordGate.AccountMatches("admin", "Admin"));
        Assert.True(PasswordGate.AccountMatches("admin", " admin "));
        Assert.True(PasswordGate.AccountMatches(" Дежурный ", "дежурный"));
    }

    [Fact]
    public void A_renamed_account_replaces_the_default_one()
    {
        Assert.True(PasswordGate.AccountMatches("дежурный", "дежурный"));
        Assert.False(PasswordGate.AccountMatches("дежурный", "admin"));
    }

    [Fact]
    public void An_empty_entry_never_matches()
    {
        Assert.False(PasswordGate.AccountMatches("admin", ""));
        Assert.False(PasswordGate.AccountMatches("admin", null));
    }

    [Theory]
    [InlineData("мусор")]
    [InlineData("100000:не-base64:тоже")]
    [InlineData("0::")]
    [InlineData("abc:def:ghi")]
    public void A_broken_record_lets_nobody_in(string stored)
    {
        Assert.False(PasswordGate.Matches(stored, PasswordGate.Fallback));
        Assert.False(PasswordGate.Matches(stored, "станция-2026"));
    }
}
