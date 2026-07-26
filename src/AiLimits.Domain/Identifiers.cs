// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace AiLimits.Domain;

public readonly record struct ProviderId
{
    public string Value { get; }

    public ProviderId(string value)
    {
        Value = Require(value, "value");
    }

    public override string ToString()
    {
        return Value;
    }

    private static string Require(string value, string name)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value.Trim();
        }
        throw new ArgumentException("Identifier cannot be empty.", name);
    }
}

public readonly record struct ServiceProviderId
{
    public string Value { get; }

    public ServiceProviderId(string value)
    {
        Value = Require(value, "value");
    }

    public override string ToString()
    {
        return Value;
    }

    private static string Require(string value, string name)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value.Trim();
        }
        throw new ArgumentException("Identifier cannot be empty.", name);
    }
}

public readonly record struct AccountKey
{
    public ProviderId Provider { get; init; }

    public string Value { get; }

    public AccountKey(ProviderId Provider, string Value)
    {
        this.Provider = Provider;
        if (string.IsNullOrWhiteSpace(Value))
        {
            throw new ArgumentException("Account identifier cannot be empty.", "Value");
        }
        this.Value = Value.Trim();
    }

    public override string ToString()
    {
        return Provider.Value + ":" + Value;
    }

    public static bool TryParse(string? text, [NotNullWhen(true)] out AccountKey? account)
    {
        account = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        int num = text.IndexOf(':');
        if (num <= 0 || num == text.Length - 1)
        {
            return false;
        }
        try
        {
            account = new AccountKey(new ProviderId(text.Substring(0, num)), text.Substring(num + 1));
        }
        catch (ArgumentException)
        {
            account = null;
            return false;
        }
        return true;
    }

    [CompilerGenerated]
    public void Deconstruct(out ProviderId Provider, out string Value)
    {
        Provider = this.Provider;
        Value = this.Value;
    }
}

public readonly record struct MeterKey
{
    public string Value { get; }

    public MeterKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Meter identifier cannot be empty.", "value");
        }
        Value = value.Trim();
    }

    public override string ToString()
    {
        return Value;
    }
}
