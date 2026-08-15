// SPDX-License-Identifier: Apache-2.0
namespace AiLimits.Presentation.WinUI;

public interface IStartupRegistrationService
{
    bool IsStartupEnabled { get; }
    bool SetStartupEnabled(bool enabled);
}
