// ── 核心基础 ──
global using System;
global using System.Collections.Generic;
global using System.ComponentModel;
global using System.Diagnostics;
global using System.Globalization;
global using System.IO;
global using System.Linq;
global using System.Runtime.InteropServices;
global using System.Text;
global using System.Text.Json;
global using System.Text.RegularExpressions;
global using System.Threading.Tasks;

// ── WPF 命名空间 ──
global using System.Windows;
global using System.Windows.Controls;
global using System.Windows.Controls.Primitives;
global using System.Windows.Data;
global using System.Windows.Input;
global using System.Windows.Interop;
global using System.Windows.Media;
global using System.Windows.Media.Animation;
global using System.Windows.Threading;

// ── 类型冲突解决：WPF 优先 ──
global using WpfApp = System.Windows.Application;
global using WpfClipboard = System.Windows.Clipboard;
