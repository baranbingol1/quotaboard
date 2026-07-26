// SPDX-License-Identifier: Apache-2.0
using AiLimits.Domain;

namespace AiLimits.Infrastructure.Providers.Shared;

public sealed record MeterAlias(string Key, string DisplayName, MeterScope? Scope = null, MeterUnit? Unit = null, bool PercentIsAbsolute = false);
