// SPDX-License-Identifier: Apache-2.0
namespace AiLimits.Tests;

/// <summary>
/// Serializes tests that mutate process-wide CLINE_* environment variables so
/// they cannot observe each other mid-flight.
/// </summary>
[CollectionDefinition("ClineEnv")]
public sealed class ClineEnvCollection;
