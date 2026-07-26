// SPDX-License-Identifier: Apache-2.0
using System.Globalization;
using System.Text;

namespace AiLimits.Application.Pricing;

public static class ModelIdNormalizer
{
    public static string Normalize(string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId, "modelId");
        string text = modelId.Trim().Normalize(NormalizationForm.FormKC).ToLower(CultureInfo.InvariantCulture);
        StringBuilder stringBuilder = new StringBuilder(text.Length);
        bool flag = false;
        string text2 = text;
        foreach (char c in text2)
        {
            if (char.IsLetterOrDigit(c) || c == '.')
            {
                if (flag && stringBuilder.Length > 0)
                {
                    stringBuilder.Append('-');
                }
                stringBuilder.Append(c);
                flag = false;
            }
            else
            {
                flag = stringBuilder.Length > 0;
            }
        }
        return stringBuilder.ToString().TrimEnd('-');
    }
}
