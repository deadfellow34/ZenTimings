# Third-party notices

ZenTimings is licensed under the **GNU General Public License v3.0** — see [LICENSE](LICENSE).

It is distributed with the components below. Every one of them is GPL-3.0 compatible: MPL-2.0 permits
distribution under a Secondary License (§3.3), Apache-2.0 is compatible with GPLv3 (though not with
GPLv2), and PawnIO's "version 2 or any later version" grant allows GPLv3.

Full licence texts are in [`licenses/`](licenses/), except the two that ship with their own file.

| Component | Version | Licence | Text | Source |
|---|---|---|---|---|
| ZenStates-Core | 1.90.550 | GPL-3.0 | [LICENSE](LICENSE) | https://github.com/irusanov/ZenStates-Core |
| LibreHardwareMonitorLib | 0.8.7 | MPL-2.0 | [licenses/MPL-2.0.txt](licenses/MPL-2.0.txt) | https://github.com/LibreHardwareMonitor/LibreHardwareMonitor |
| OpenHardwareMonitorLib | 0.9.6 | MPL-2.0 | [licenses/MPL-2.0.txt](licenses/MPL-2.0.txt) | https://github.com/openhardwaremonitor/openhardwaremonitor |
| HidSharp | 2.1.0 | Apache-2.0 | [licenses/Apache-2.0.txt](licenses/Apache-2.0.txt), [licenses/HidSharp-NOTICE.txt](licenses/HidSharp-NOTICE.txt) | http://software.seekye.com/hidsharp |
| AdonisUI, AdonisUI.ClassicTheme | 1.17.1 | MIT | [licenses/AdonisUI-MIT.txt](licenses/AdonisUI-MIT.txt) | https://github.com/benruehl/adonis-ui |
| InpOut32/64 | — | MIT | `InpOut.LICENSE.txt` | http://www.highrez.co.uk/downloads/inpout32/ |
| PawnIO | 2.2.0 | GPL-2.0-or-later | `PawnIO.LICENSE.txt` | https://github.com/namazso/PawnIO.Setup |

## Corresponding source

`ZenStates-Core.dll` is shipped as a prebuilt binary and is itself GPL-3.0. Its complete corresponding
source is at **https://github.com/irusanov/ZenStates-Core**. The binary in `Common/` is unmodified.

`LibreHardwareMonitorLib.dll`, `OpenHardwareMonitorLib.dll` and `HidSharp.dll` are likewise unmodified
upstream builds; source is available at the URLs above.

## Not bundled

The AMD Ryzen Master Monitoring SDK is deliberately **not** part of this repository or its releases. It
is proprietary and carries its own EULA, which cannot be combined with GPL-3.0.
