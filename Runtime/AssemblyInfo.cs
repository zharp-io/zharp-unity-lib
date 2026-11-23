using System.Runtime.CompilerServices;
using UnityEngine.Scripting;

// The settings page and the build step use the settings code; the edit-mode tests use the core.
[assembly: InternalsVisibleTo("Zharp.Unity.Editor")]
[assembly: InternalsVisibleTo("Zharp.Unity.Tests.Editor")]
// Nothing here is reached by reflection, but the callbacks Unity calls by name must survive
// managed code stripping on its highest level.
[assembly: AlwaysLinkAssembly]
