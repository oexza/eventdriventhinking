﻿using System.Runtime.CompilerServices;
using EventDrivenThinking.Reflection;
using FluentAssertions;
using Xunit;

[assembly: InternalsVisibleTo("DynamicMarkupAssembly")]

namespace EventDrivenThinking.Tests.Convention2Interface
{
    interface IFoo2<out D, in T>
    {
        D Hello(T arg);
    }
    interface IFoo<in T, out D>
    {
        D Hello(T arg);
    }

    public class Foo
    {
        public virtual string Hello(string arg)
        {
            return arg;
        }
    }

    

    public class MarkupClassFactoryTests
    {
        [Fact]
        public void CheckInheritance()
        {
            MarkupOpenGenericFactory openGenericFactory = MarkupOpenGenericFactory.Create(typeof(Foo), typeof(IFoo<,>));

            var sut = openGenericFactory.Create<IFoo<string,string>>();

            sut.Hello("hello").Should().Be("hello");

           
        }

        [Fact]
        public void CheckInverseInheritance()
        {
            MarkupOpenGenericFactory openGenericFactory2 = MarkupOpenGenericFactory.Create(typeof(Foo), typeof(IFoo2<,>));

            var sut2 = openGenericFactory2.Create<IFoo2<string, string>>();

            var actual = sut2.Hello("hello");
            sut2.Hello("hello").Should().Be("hello");
        }
    }
}
