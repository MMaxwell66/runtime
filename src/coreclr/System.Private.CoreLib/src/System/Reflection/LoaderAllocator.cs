// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace System.Reflection
{
/* 💡💡💡💡💡(很有意思的点哦)
这里用scout，而不是在Allocator的Finalizer去调用是因为Finalizer其实是一个很弱的机制。考虑一下 class A -> LoaderAllocator，class A的一个实例a有一个LoaderAllocator的引用（可以通过反射的拿到internal），然后a和LoaderAllocator被回收了，这时候a和LoaderAllocator的Finalizer都会执行。然后a的Finalizer里面是可以resurrect自己的（比如把a存到某个static上去），这样的话即使LoaderAllocator的Finalizer执行过了。但是因为a活着，a有对LoaderAllocator的引用，其实LoaderAllocator还是活着。这是因为Finalizer里面可以执行的代码很任意，所以导致了一种resurrect的现象。
下面提到了解决方案是使用long weak handler，它可以保证LoaderAllocator一定完完全全的死掉了。但是这个就不能在LoaderAllocator的Finalizer里面去调native api了。所以要有一个scout，然后scout使用了`GC.ReRegisterForFinalize`来处理resurrect下的重试问题。
*/
    //
    // We can destroy the unmanaged part of collectible type only after the managed part is definitely gone and thus
    // nobody can call/allocate/reference anything related to the collectible assembly anymore. A call to finalizer
    // alone does not guarantee that the managed part is gone. A malicious code can keep a reference to some object
    // in a way that it survives finalization, or we can be running during shutdown where everything is finalized.
    //
    // The unmanaged LoaderAllocator keeps a reference to the managed LoaderAllocator in long weak handle. If the long
    // weak handle is null, we can be sure that the managed part of the LoaderAllocator is definitely gone and that it
    // is safe to destroy the unmanaged part. Unfortunately, we can not perform the above check in a finalizer on the
    // LoaderAllocator, but it can be performed on a helper object.
    //
    // The finalization does not have to be done using CriticalFinalizerObject. We have to go over all LoaderAllocators
    // during AppDomain shutdown anyway to avoid leaks e.g. if somebody stores reference to LoaderAllocator in a static.
    //
    internal sealed partial class LoaderAllocatorScout
    {
        // This field is set by the VM to atomically transfer the ownership to the managed loader allocator
        internal IntPtr m_nativeLoaderAllocator;

        [LibraryImport(RuntimeHelpers.QCall, EntryPoint = "LoaderAllocator_Destroy")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool Destroy(IntPtr nativeLoaderAllocator);

        ~LoaderAllocatorScout()
        {
            if (m_nativeLoaderAllocator == IntPtr.Zero)
                return;

            // Destroy returns false if the managed LoaderAllocator is still alive.
            if (!Destroy(m_nativeLoaderAllocator))
            {
                // Somebody might have been holding a reference on us via weak handle.
                // We will keep trying. It will be hopefully released eventually.
                GC.ReRegisterForFinalize(this);
            }
        }
    }

    internal sealed class LoaderAllocator
    {
        private LoaderAllocator()
        {
            m_slots = new object[5];
            // m_slotsUsed = 0;

            m_scout = new LoaderAllocatorScout();
        }

#pragma warning disable CA1823, 414, 169
        private LoaderAllocatorScout m_scout;
        private object[] m_slots;
        internal CerHashtable<RuntimeMethodInfo, RuntimeMethodInfo> m_methodInstantiations;
        private int m_slotsUsed;
#pragma warning restore CA1823, 414, 169
    }
}
