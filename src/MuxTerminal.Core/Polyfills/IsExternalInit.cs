#if NETFRAMEWORK
// Нужен компилятору для record и init-свойств на .NET Framework.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}
#endif
