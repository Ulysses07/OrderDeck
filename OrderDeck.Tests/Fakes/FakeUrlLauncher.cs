using System;
using System.Collections.Generic;
using LiveDeck.Core.Customers;

namespace LiveDeck.Tests.Fakes;

/// <summary>Test double — kayıt tutar, optionally throws.</summary>
public sealed class FakeUrlLauncher : IUrlLauncher
{
    public List<string> LaunchedUrls { get; } = new();
    public Exception? ThrowOnLaunch { get; set; }

    public void Launch(string url)
    {
        if (ThrowOnLaunch is not null) throw ThrowOnLaunch;
        LaunchedUrls.Add(url);
    }
}
