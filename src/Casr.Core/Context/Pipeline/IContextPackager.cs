using Casr.Core.Context.Capabilities;
using Casr.Core.Context.Models;
using Casr.Core.Models;

namespace Casr.Core.Context.Pipeline;

public interface IContextPackager
{
    CanonicalContext Normalize(CanonicalSession session);

    /// <summary>Packages with the legacy (pre-conversion-dialog) behavior — see <see cref="ConversionOptions.Legacy"/>.</summary>
    CanonicalSession Package(CanonicalSession source, HarnessCapabilities targetCapabilities)
        => Package(source, targetCapabilities, ConversionOptions.Legacy);

    /// <summary>Packages a session for a target harness under explicit conversion options.</summary>
    CanonicalSession Package(CanonicalSession source, HarnessCapabilities targetCapabilities, ConversionOptions options);
}
