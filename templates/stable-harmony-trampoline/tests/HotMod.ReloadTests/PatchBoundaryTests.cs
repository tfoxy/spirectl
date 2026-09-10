using System.Reflection;
using HotMod.Shell;
using HotMod.Shell.HarmonyPatches;
using HotMod.Shell.Runtime;
using Xunit;

namespace HotMod.ReloadTests;

public sealed class PatchBoundaryTests
{
    [Fact]
    public void HarmonyPatchTypesDoNotReferenceReloadableLogicAssembly()
    {
        var patchTypes = typeof(ExampleCombatTurnPatch).Assembly.GetTypes()
            .Where(type => type.Namespace == "HotMod.Shell.HarmonyPatches")
            .ToArray();

        Assert.NotEmpty(patchTypes);
        foreach (var type in patchTypes)
        {
            AssertNoLogicReference(type);
            foreach (var memberType in type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                         .SelectMany(ReferencedTypes))
            {
                AssertNoLogicReference(memberType);
            }
        }
    }

    [Fact]
    public void OfflinePatchDispatchDoesNotThrowWithoutLoadedLogic()
    {
        ExampleCombatTurnPatch.DispatchForTest();
    }

    [Fact]
    public void PatchOwnerValidationReportsStructuredRestartFailure()
    {
        var error = ModEntry.ValidatePatchOwnerId(" ");

        Assert.NotNull(error);
        Assert.Equal(ReloadErrorCode.ReloadPatchOwnerMissing, error.Code);
        Assert.True(error.RestartRequired);
    }

    [Fact]
    public void PatchExceptionReportsHookSignatureChangedRestartFailure()
    {
        var error = ModEntry.CreatePatchFailure(new MissingMethodException("Combat.NCombat", "StartTurn"));

        Assert.Equal(ReloadErrorCode.ReloadHookSignatureChanged, error.Code);
        Assert.Equal(ReloadPhase.Patch, error.Phase);
        Assert.True(error.RestartRequired);
    }

    private static IEnumerable<Type> ReferencedTypes(MemberInfo member)
    {
        return member switch
        {
            FieldInfo field => [field.FieldType],
            PropertyInfo property => [property.PropertyType],
            MethodInfo method => method.GetParameters().Select(parameter => parameter.ParameterType).Append(method.ReturnType),
            ConstructorInfo constructor => constructor.GetParameters().Select(parameter => parameter.ParameterType),
            EventInfo eventInfo => [eventInfo.EventHandlerType!],
            _ => []
        };
    }

    private static void AssertNoLogicReference(Type type)
    {
        Assert.NotEqual("HotMod.Logic", type.Assembly.GetName().Name);
    }
}
