# Third-party notices

G.H.U.D. is licensed under the GNU General Public License v3.0 or later (see `LICENSE`).
It includes the following components under their own licences. Their notices must stay
with any copy of G.H.U.D. that includes them (for example a released `.exe`).

| Component | Used for | Licence | Copyright |
|---|---|---|---|
| [SharpCompress](https://github.com/adamhathcock/sharpcompress) 1.0.0 | zip / 7z / rar / tar archives | MIT | Copyright (c) 2025 Adam Hathcock |
| [MegaApiClient](https://github.com/gpailler/MegaApiClient) 1.10.5 | MEGA links | MIT | Copyright (c) 2021 Gregoire Pailler |
| [Newtonsoft.Json](https://github.com/JamesNK/Newtonsoft.Json) 13.0.4 | needed by MegaApiClient | MIT | Copyright (c) 2007 James Newton-King |
| [.NET runtime](https://github.com/dotnet/runtime) 10 | included in self-contained builds | MIT | Copyright (c) .NET Foundation and Contributors |

Components used only to build or test (the .NET SDK, xUnit) are not shipped and are not
listed. When the window is added, its libraries (Avalonia and its graphics libraries,
CommunityToolkit.Mvvm, ...) are added here.

## MIT License

The MIT-licensed components above are provided under these terms, with each
component's copyright line from the table:

```
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
