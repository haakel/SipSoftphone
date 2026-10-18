# SipSoftphone 📱

**A modern SIP softphone client with a mobile-like interface**, designed for Windows desktop platforms. Features an intuitive phone-inspired UI similar to popular VoIP apps like 3CX.

![Platform](https://img.shields.io/badge/platform-Windows-blue)
![Framework](https://img.shields.io/badge/framework-.NET%2010.0-green)
![UI](https://img.shields.io/badge/ui-WPF-orange)

---

## 🌟 Features

### 📞 **Complete Phone Functionality**
- Make and receive SIP calls
- HD voice support (multiple codecs)
- Call hold, transfer, mute
- Incoming call alerts
- Call history with filters

### 🎨 **Mobile-Like UI Design**
- Smartphone-inspired interface (400×780px)
- Bottom tab navigation (Calls, Contacts, History, Logs)
- Rounded corners and modern aesthetics
- Touch-friendly button sizes
- Clean, clutter-free design

### 👥 **Contact Management**
- Contact list with search
- Speed dial favorites
- Avatars for contacts
- Quick dial on double-click

### 📋 **Call History**
- Detailed call logs
- Filter by: All, Missed, Incoming, Outgoing
- Search functionality
- One-click redial
- Auto-import from log files

### 🔧 **Multi-Account Support**
- Multiple SIP accounts
- Easy account switching
- Account registration status
- Persistent configuration

### 📝 **System Logs**
- Real-time SIP, Audio, Call logs
- Filterable log categories
- Copy/clear functionality
- Pause logging feature

---

## 🏗️ Architecture

```
SipSoftphone/
├── MainWindow.xaml          # Mobile-style UI layout
├── MainViewModel.cs         # MVVM ViewModel logic
├── App.xaml                 # Application styles & resources
├── PhoneEngine.cs           # Core phone logic
├── AudioEngine.cs           # Audio processing
├── ContactsBook.cs          # Contact management
├── SseServer.cs             # SSE event broadcasting
└── config.json              # Configuration file
```

### Tech Stack
- **.NET 10.0** - Modern .NET platform
- **WPF (Windows Presentation Foundation)** - Desktop UI framework
- **MVVM Pattern** - Model-View-ViewModel architecture
- **NAudio** - Audio processing
- **SIPSorcery** - SIP protocol implementation
- **Server-Sent Events** - Real-time event streaming

---

## 🚀 Installation & Build

### Prerequisites
- **Windows 10/11**
- **.NET 10.0 SDK** or Visual Studio 2022+
- Network access for SIP server connection

### Option 1: Using Visual Studio (Recommended)
1. Open `SipSoftphone.csproj` in Visual Studio 2022
2. Restore NuGet packages
3. Press `F5` to build and run

### Option 2: Using Command Line
```bash
# Clone repository
git clone https://github.com/yourusername/sipsoftphone.git
cd sipsoftphone

# Build
dotnet build

# Run
dotnet run
```

### Option 3: Publish Standalone EXE
```bash
# Create standalone executable
dotnet publish -c Release -r win-x64 --self-contained false
```

---

## ⚙️ Configuration

Create a `config.json` file in the application directory:

```json
{
  "sip": {
    "accounts": [
      {
        "name": "Work",
        "server": "sip.example.com",
        "username": "1001",
        "password": "secret"
      }
    ],
    "activeAccountIndex": 0
  },
  "hook": {
    "logFile": "call_history.txt",
    "eventsEnabled": true
  },
  "ui": {
    "rtl": false
  }
}
```

---

## 📱 User Interface

### Navigation Tabs
1. **📞 Calls** - Dial pad, current call display, in-call controls
2. **👥 Contacts** - Contact list, speed dials, add/remove contacts
3. **📋 History** - Call history with filters and search
4. **📝 Logs** - System logs (SIP, Audio, Calls, Errors)

### Key Features
- **Phone Frame**: Rounded corners mimicking smartphone design
- **Bottom Navigation**: Intuitive tab switching with icons
- **Page Indicators**: Visual dots showing active page
- **Responsive**: Adapts to different screen sizes

---

## 🔄 Usage Guide

### Making a Call
1. Go to **Calls** tab
2. Enter number using dial pad or type directly
3. Click 📞 button or press Enter
4. Answer incoming calls with **Answer** button

### Adding Contacts
1. Go to **Contacts** tab
2. Click ➕ button
3. Enter name and phone number
4. Optionally mark as speed dial
5. Click **Add**

### Call History Filters
- **All**: Show all calls
- **Missed**: Only missed calls
- **Incoming**: Received calls
- **Outgoing**: Made calls

---

## 🐛 Troubleshooting

### Common Issues

**Cannot register:**
- Check internet connection
- Verify SIP server address
- Confirm username/password
- Check firewall settings

**No audio:**
- Verify microphone/speaker selection in Settings
- Check system audio permissions
- Ensure no other app is using audio devices

**Logs not loading:**
- Verify log file path in config.json
- Check file permissions

---

## 🤝 Contributing

Contributions are welcome! Please follow these steps:

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/amazing-feature`)
3. Commit changes (`git commit -m 'Add amazing feature'`)
4. Push to branch (`git push origin feature/amazing-feature`)
5. Open a Pull Request

---

## 📄 License

This project is licensed under the MIT License - see the LICENSE file for details.

---

## 🙏 Acknowledgments

- **NAudio** - For audio processing capabilities
- **SIPSorcery** - For SIP protocol implementation
- **Open-source community** - For various libraries and resources

---

## 📞 Version Information

- **Current Version**: Development (dev branch)
- **Latest Update**: Mobile UI redesign with tabbed navigation
- **Status**: Active development

---

## 📮 Support

For issues, questions, or suggestions:
- Open an issue on GitHub
- Contact the maintainers
- Check existing documentation

---

<div align="center">

**Made with ❤️ for modern communication**

</div>
