namespace SipSoftphone;

/// <summary>
/// Abstraction the UI talks to instead of a concrete SIP stack.
/// <see cref="PhoneEngine"/> is the current implementation; dropping in another SIP
/// backend means implementing this interface — no view or view-model changes required.
/// The frontend never touches SIP signalling or media directly.
/// </summary>
public interface ICallService : IDisposable
{
    // ---------- state ----------
    CallSnapshot CurrentCall { get; }
    RegistrationStatus Registration { get; }
    string ServerInfo { get; }

    /// <summary>Event bus feeding call-history / integration hooks; null when disabled.</summary>
    CallEventBus? Bus { get; }

    // ---------- lifecycle ----------
    bool Start();
    bool Restart();
    void Register();
    void Unregister();

    // ---------- call control ----------
    Task<bool> CallAsync(string number);
    Task AnswerPendingAsync();
    void RejectPending();
    void Hangup();
    void Hold();
    void Resume();
    bool ToggleHold();
    Task<bool> BlindTransferAsync(string target);
    Task SendDtmfAsync(char key);

    /// <summary>Mute / unmute the microphone for the current call.</summary>
    void SetMicrophoneMuted(bool isMuted);

    // ---------- events (may fire on background threads) ----------
    event Action<string>? Log;
    event Action<CallSnapshot>? CallChanged;
    event Action<RegistrationStatus>? RegistrationChanged;
    event Action<string, string?>? IncomingAlert;
}
