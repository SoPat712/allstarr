using System.Reflection;
using allstarr.Controllers;

namespace allstarr.Tests;

public sealed class FavoriteFileSafetyTests
{
    [Fact]
    public void JellyfinController_HasNoImplicitPendingDeletionProcessor()
    {
        var methods = typeof(JellyfinController).GetMethods(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        Assert.DoesNotContain(methods, method => method.Name == "MarkTrackForDeletionAsync");
        Assert.DoesNotContain(methods, method => method.Name == "ProcessPendingDeletionsAsync");
        Assert.DoesNotContain(methods, method => method.Name == "ActuallyDeleteTrackAsync");
    }
}
