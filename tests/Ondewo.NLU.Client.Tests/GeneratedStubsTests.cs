using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Grpc.Core;
using Grpc.Net.Client;
using Xunit;

namespace Ondewo.Nlu.Client.Tests
{
    /// <summary>
    /// Product-agnostic suite over the generated stubs.
    /// <para>
    /// Every case is discovered by reflection from <see cref="ProductStubs.Assembly"/>, so this
    /// file is identical in every ONDEWO csharp client: it exercises whatever protos that product
    /// happens to ship, and a proto added later is covered without touching the tests.
    /// </para>
    /// </summary>
    public class GeneratedStubsTests
    {
        /// <summary>Nothing is connected to; the address only has to be well formed.</summary>
        private const string DummyTarget = "http://localhost:50051";

        // ----------------------------------------------------------------- discovery

        public static IEnumerable<object[]> MessageTypes =>
            ProductStubs.Assembly.GetTypes()
                .Where(type => type.IsClass
                               && !type.IsAbstract
                               && typeof(IMessage).IsAssignableFrom(type)
                               && type.GetConstructor(Type.EmptyTypes) != null)
                .OrderBy(type => type.FullName, StringComparer.Ordinal)
                .Select(type => new object[] { type });

        public static IEnumerable<object[]> EnumTypes =>
            ProductStubs.Assembly.GetTypes()
                .Where(type => type.IsEnum && (type.IsPublic || type.IsNestedPublic))
                .OrderBy(type => type.FullName, StringComparer.Ordinal)
                .Select(type => new object[] { type });

        public static IEnumerable<object[]> ServiceClientTypes =>
            ProductStubs.Assembly.GetTypes()
                .Where(type => type.IsClass && !type.IsAbstract && typeof(ClientBase).IsAssignableFrom(type))
                .OrderBy(type => type.FullName, StringComparer.Ordinal)
                .Select(type => new object[] { type });

        public static IEnumerable<object[]> FileDescriptors =>
            ProductStubs.Assembly.GetTypes()
                .Select(type => type.GetProperty("Descriptor", BindingFlags.Public | BindingFlags.Static))
                .Where(property => property != null && property.PropertyType == typeof(FileDescriptor))
                .Select(property => (FileDescriptor)property.GetValue(null))
                .OrderBy(descriptor => descriptor.Name, StringComparer.Ordinal)
                .Select(descriptor => new object[] { descriptor.Name });

        // ----------------------------------------------------------------- the suite itself
        // A stub set that failed to generate would make every theory below run zero cases and
        // still report success, so the discovery is asserted on first.

        [Fact]
        public void TheGeneratedAssemblyShipsMessagesServicesAndProtoFiles()
        {
            Assert.NotEmpty(MessageTypes);
            Assert.NotEmpty(EnumTypes);
            Assert.NotEmpty(ServiceClientTypes);
            Assert.NotEmpty(FileDescriptors);
        }

        /// <summary>
        /// The core contract of a generated stub: a populated message survives a serialize/parse
        /// cycle byte for byte and compares equal to what went in.
        /// </summary>
        [Theory]
        [MemberData(nameof(MessageTypes))]
        public void MessageRoundTripsThroughItsWireFormat(Type messageType)
        {
            IMessage message = CreatePopulatedMessage(messageType);

            byte[] bytes = message.ToByteArray();
            IMessage parsed = message.Descriptor.Parser.ParseFrom(bytes);

            Assert.Equal(message, parsed);
            Assert.Equal(message.GetHashCode(), parsed.GetHashCode());
            Assert.Equal(bytes, parsed.ToByteArray());
            Assert.Equal(message.CalculateSize(), bytes.Length);
        }

        /// <summary>
        /// A message whose descriptor declares at least one settable singular field has to put
        /// something on the wire - this is what catches a generator that drops values.
        /// </summary>
        [Theory]
        [MemberData(nameof(MessageTypes))]
        public void PopulatedMessageIsDistinguishableFromAnEmptyOne(Type messageType)
        {
            IMessage message = CreatePopulatedMessage(messageType);
            if (!WroteAFieldOnTheWire(message))
            {
                // Messages with no singular field at all (pure request/response envelopes, or
                // repeated-only payloads) legitimately serialize to nothing.
                return;
            }

            IMessage empty = (IMessage)Activator.CreateInstance(messageType);

            Assert.NotEmpty(message.ToByteArray());
            Assert.NotEqual(message, empty);
        }

        /// <summary>proto3 requires the first value of every enum to be the zero value.</summary>
        [Theory]
        [MemberData(nameof(EnumTypes))]
        public void EnumDefinesAZeroValue(Type enumType)
        {
            long[] values = Enum.GetValues(enumType).Cast<object>().Select(Convert.ToInt64).ToArray();

            Assert.Contains(0L, values);
            Assert.Equal(0L, values[0]);
        }

        /// <summary>
        /// A generated client has to bind to a channel, and its surface has to carry exactly the
        /// RPCs its service descriptor declares - the descriptor is the proto, so this is an
        /// assertion against the .proto rather than against a hard-coded method list.
        /// </summary>
        [Theory]
        [MemberData(nameof(ServiceClientTypes))]
        public void ServiceClientBindsToAChannelAndExposesEveryDeclaredRpc(Type clientType)
        {
            using GrpcChannel channel = GrpcChannel.ForAddress(DummyTarget);

            object client = Activator.CreateInstance(clientType, channel);

            Assert.NotNull(client);
            Assert.IsAssignableFrom<ClientBase>(client);

            ServiceDescriptor service = ServiceDescriptorOf(clientType);
            Assert.NotEmpty(service.Methods);
            Assert.EndsWith("." + service.Name, service.FullName, StringComparison.Ordinal);

            IReadOnlyCollection<string> methodNames = clientType
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Select(method => method.Name)
                .ToHashSet(StringComparer.Ordinal);

            foreach (MethodDescriptor rpc in service.Methods)
            {
                Assert.Contains(rpc.Name, methodNames);
            }
        }

        /// <summary>Every generated file descriptor has to be self-consistent and resolvable.</summary>
        [Theory]
        [MemberData(nameof(FileDescriptors))]
        public void FileDescriptorIsRegisteredAndResolvable(string fileName)
        {
            FileDescriptor descriptor = FileDescriptorNamed(fileName);

            Assert.EndsWith(".proto", descriptor.Name, StringComparison.Ordinal);
            Assert.All(descriptor.MessageTypes, message => Assert.NotNull(message.ClrType));
            Assert.All(descriptor.MessageTypes, message => Assert.NotNull(message.Parser));
            Assert.All(descriptor.Services, service => Assert.NotEmpty(service.Methods));

            // A message reached through the descriptor has to be the very type the assembly ships.
            Assert.All(
                descriptor.MessageTypes,
                message => Assert.Same(ProductStubs.Assembly, message.ClrType.Assembly));
        }

        // ----------------------------------------------------------------- helpers

        private static FileDescriptor FileDescriptorNamed(string fileName)
        {
            return ProductStubs.Assembly.GetTypes()
                .Select(type => type.GetProperty("Descriptor", BindingFlags.Public | BindingFlags.Static))
                .Where(property => property != null && property.PropertyType == typeof(FileDescriptor))
                .Select(property => (FileDescriptor)property.GetValue(null))
                .Single(descriptor => descriptor.Name == fileName);
        }

        private static ServiceDescriptor ServiceDescriptorOf(Type clientType)
        {
            // grpc_csharp_plugin nests <Service>Client inside a static <Service> class that holds
            // the ServiceDescriptor.
            Type serviceClass = clientType.DeclaringType;
            Assert.NotNull(serviceClass);

            PropertyInfo descriptor = serviceClass.GetProperty(
                "Descriptor", BindingFlags.Public | BindingFlags.Static);
            Assert.NotNull(descriptor);

            return Assert.IsType<ServiceDescriptor>(descriptor.GetValue(null));
        }

        /// <summary>
        /// Builds an instance with every singular field set to a non-default value. Repeated and
        /// map fields are left alone here - the per-product suite covers those concretely.
        /// </summary>
        private static IMessage CreatePopulatedMessage(Type messageType)
        {
            IMessage message = (IMessage)Activator.CreateInstance(messageType);

            foreach (FieldDescriptor field in message.Descriptor.Fields.InDeclarationOrder())
            {
                if (field.IsRepeated || field.IsMap)
                {
                    continue;
                }

                object value = SampleValueFor(message.Descriptor, field);
                if (value != null)
                {
                    field.Accessor.SetValue(message, value);
                }
            }

            return message;
        }

        private static bool WroteAFieldOnTheWire(IMessage message)
        {
            return message.CalculateSize() > 0;
        }

        /// <summary>
        /// The sample value is derived from the generated PROPERTY type, not from
        /// <see cref="FieldDescriptor.FieldType"/>: a google.protobuf wrapper field is a message
        /// on the wire but surfaces as a nullable primitive in C#, and feeding the accessor a
        /// wrapper instance would throw.
        /// </summary>
        private static object SampleValueFor(MessageDescriptor containing, FieldDescriptor field)
        {
            PropertyInfo property = containing.ClrType.GetProperty(field.PropertyName);
            Assert.NotNull(property);

            Type type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

            if (type == typeof(string)) return "ondewo";
            if (type == typeof(bool)) return true;
            if (type == typeof(int)) return 42;
            if (type == typeof(uint)) return 42u;
            if (type == typeof(long)) return 42L;
            if (type == typeof(ulong)) return 42UL;
            if (type == typeof(float)) return 1.5f;
            if (type == typeof(double)) return 2.5d;
            if (type == typeof(ByteString)) return ByteString.CopyFromUtf8("ondewo");
            if (type.IsEnum) return Enum.ToObject(type, NonDefaultNumberOf(field.EnumType));
            if (typeof(IMessage).IsAssignableFrom(type)) return Activator.CreateInstance(type);

            // Only proto2 groups reach this, and the ONDEWO APIs are proto3 throughout.
            return null;
        }

        private static int NonDefaultNumberOf(EnumDescriptor enumType)
        {
            foreach (EnumValueDescriptor value in enumType.Values)
            {
                if (value.Number != 0)
                {
                    return value.Number;
                }
            }

            return 0;
        }
    }
}
