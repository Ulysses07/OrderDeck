namespace OrderDeck.App.Services.Chat;

/// <summary>"Sohbeti yenile" düğmesinin evresi.</summary>
public enum ChatRefreshPhase
{
    /// <summary>Düğme boşta; basılabilir.</summary>
    Idle,

    /// <summary>Yenileme sürüyor: düğmede halka döner.</summary>
    Busy,

    /// <summary>Yenileme sorunsuz bitti: düğmede kısa süre onay işareti durur.</summary>
    Done,
}

/// <summary>
/// "Sohbeti yenile" düğmesinin ne kadar meşgul kalacağını ve sonunda onay işareti gösterip
/// göstermeyeceğini izler (tasarım: Sohbeti yenile §5.5).
///
/// <para>Saf: saat okumaz, zamanlayıcı kurmaz. Çağıran her tıkta o anı ve satırların durumunu verir.</para>
///
/// <list type="bullet">
/// <item>Meşgul hâl en az <c>minimum</c> sürer (yarım saniyede biten yenileme düğmeyi titretirdi) ve
/// hiçbir satır "bağlanıyor"da kalmayınca biter.</item>
/// <item><c>maximum</c> dolunca düğme boşa döner: bağlanamayan kaynak düğmeyi kilitlemez, kendi
/// döngüsünde denemeyi sürdürür. Onay işareti gösterilmez.</item>
/// <item>Bitişte sorun durumunda satır yoksa <c>doneFor</c> boyunca onay işareti gösterilir. Sorun
/// varsa (rengi soluk olsa da) gösterilmez: sorunu şerit söyler.</item>
/// </list>
/// </summary>
public sealed class ChatRefreshTracker(TimeSpan minimum, TimeSpan maximum, TimeSpan doneFor)
{
    /// <summary>İçinde bulunulan evrenin başladığı an.</summary>
    private DateTimeOffset _since;

    public ChatRefreshPhase Phase { get; private set; } = ChatRefreshPhase.Idle;

    /// <summary>Yenileme başladı.</summary>
    public void Start(DateTimeOffset now)
    {
        Phase = ChatRefreshPhase.Busy;
        _since = now;
    }

    /// <summary>Bir tık: evreyi o ana ve satırların durumuna göre ilerletir.</summary>
    /// <param name="now">Şimdi.</param>
    /// <param name="anyConnecting">"Bağlanıyor"da satır var mı.</param>
    /// <param name="anyProblem">Sorun durumunda satır var mı (rengi soluk olsa da).</param>
    public void Observe(DateTimeOffset now, bool anyConnecting, bool anyProblem)
    {
        switch (Phase)
        {
            case ChatRefreshPhase.Busy:
                var elapsed = now - _since;
                var gaveUp = elapsed >= maximum;
                if (!gaveUp && (elapsed < minimum || anyConnecting)) return;

                if (gaveUp || anyProblem)
                {
                    Phase = ChatRefreshPhase.Idle;
                }
                else
                {
                    Phase = ChatRefreshPhase.Done;
                    _since = now;
                }

                break;

            case ChatRefreshPhase.Done:
                if (now - _since >= doneFor) Phase = ChatRefreshPhase.Idle;
                break;
        }
    }

    /// <summary>Yenileme yarıda kesildi (yayın oturumu bitti): düğme hemen boşa döner.</summary>
    public void Cancel() => Phase = ChatRefreshPhase.Idle;
}
