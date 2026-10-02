using Casr.Core.Context.Capabilities;
using Casr.Core.Context.Models;
using Casr.Core.Models;

namespace Casr.Core.Context.Pipeline;

public interface IContextPackager
{
    CanonicalContext Normalize(CanonicalSession session);
    CanonicalSession Package(CanonicalSession source, HarnessCapabilities targetCapabilities);
}
