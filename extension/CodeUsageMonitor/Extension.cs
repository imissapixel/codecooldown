// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.CommandPalette.Extensions;

namespace CodeUsageMonitor;

[Guid("9b729ea6-1b68-48ee-90ef-91215f4165ea")]
public sealed partial class CodeUsageMonitor : IExtension, IDisposable
{
    private readonly ManualResetEvent _extensionDisposedEvent;

    private readonly CodeUsageMonitorCommandsProvider _provider = new();

    public CodeUsageMonitor(ManualResetEvent extensionDisposedEvent)
    {
        this._extensionDisposedEvent = extensionDisposedEvent;
    }

    public object? GetProvider(ProviderType providerType)
    {
        return providerType switch
        {
            ProviderType.Commands => _provider,
            _ => null,
        };
    }

    public void Dispose()
    {
        _provider.Dispose();
        _extensionDisposedEvent.Set();
    }
}
