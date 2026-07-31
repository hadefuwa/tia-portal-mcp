// Compiler-required types that do not exist in .NET Framework 4.8 mscorlib.
// The C# 9/11 compiler expects to find these; without them features like
// `init` setters and `required` members fail to compile on net48.

namespace System.Runtime.CompilerServices
{
    // C# 9 — init-only property setters
    internal static class IsExternalInit { }

    // C# 11 — required members
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct |
                    AttributeTargets.Field  | AttributeTargets.Property,
                    AllowMultiple = false, Inherited = false)]
    internal sealed class RequiredMemberAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = false)]
    internal sealed class CompilerFeatureRequiredAttribute : Attribute
    {
        public CompilerFeatureRequiredAttribute(string featureName)
            => FeatureName = featureName;
        public string FeatureName { get; }
        public bool   IsOptional  { get; set; }
    }
}

namespace System.Diagnostics.CodeAnalysis
{
    // C# 11 — marks constructors that initialise all required members
    [AttributeUsage(AttributeTargets.Constructor, AllowMultiple = false, Inherited = false)]
    internal sealed class SetsRequiredMembersAttribute : Attribute { }
}
