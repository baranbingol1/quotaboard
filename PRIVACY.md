# Privacy policy

QuotaBoard is a local-first Windows application. It reads supported AI tools'
local configuration, authentication state, subscription-limit information, and
usage history so it can display limits, reset times, and token usage.

## Network access

QuotaBoard makes network requests when it retrieves live information requested
by the user or refreshes data for a configured provider. Those requests go
directly to the relevant provider service. QuotaBoard also retrieves provider
status information and the public model-pricing catalog from
[models.dev](https://models.dev/). Information sent to those services is
governed by their respective privacy policies and terms.

QuotaBoard does not operate a project-owned account backend and does not include
project-owned telemetry, analytics, advertising, or crash-reporting services.
It does not automatically send credentials, usage information, or personal
information to the QuotaBoard maintainers.

## Local data

Settings, cached provider data, subscription snapshots, and usage history are
stored locally under `%LOCALAPPDATA%\QuotaBoard`. App-refreshed provider secrets
are stored using Windows Credential Manager. QuotaBoard reads supported tools'
existing local credentials and usage files; it does not upload those files to
the QuotaBoard maintainers.

To remove QuotaBoard, delete the application folder. To remove its database,
pricing cache, and preferences as well, delete `%LOCALAPPDATA%\QuotaBoard`.
QuotaBoard's Cline session entries are stored separately: open **Credential
Manager > Windows Credentials** and remove the Generic Credentials whose
target contains `AiLimits/cline/`. If QuotaBoard was configured to start with
Windows, disable that option before deleting the application folder.

## Contact

For privacy questions or reports, open an issue in the
[QuotaBoard GitHub repository](https://github.com/baranbingol1/quotaboard/issues).
