using System;
using MozaPlugin.Telemetry;
using Xunit;

namespace MozaPlugin.Tests.Telemetry
{
    public class PropertyCoercionTests
    {
        [Fact]
        public void Double_passes_through()
            => Assert.Equal(3.14, PropertyCoercion.Coerce(3.14, "p"));

        [Fact]
        public void Float_passes_through()
            => Assert.Equal(1.5, PropertyCoercion.Coerce(1.5f, "p"), 4);

        [Fact]
        public void Int_converts_to_double()
            => Assert.Equal(42.0, PropertyCoercion.Coerce(42, "p"));

        [Fact]
        public void Long_converts_to_double()
            => Assert.Equal(1234567890.0, PropertyCoercion.Coerce(1234567890L, "p"));

        [Fact]
        public void Bool_maps_to_one_or_zero()
        {
            Assert.Equal(1.0, PropertyCoercion.Coerce(true, "p"));
            Assert.Equal(0.0, PropertyCoercion.Coerce(false, "p"));
        }

        [Fact]
        public void TimeSpan_uses_total_seconds()
            => Assert.Equal(90.5, PropertyCoercion.Coerce(TimeSpan.FromSeconds(90.5), "p"));

        [Fact]
        public void Gear_R_maps_to_minus_one()
            => Assert.Equal(-1.0, PropertyCoercion.Coerce("R", "gear"));

        [Fact]
        public void Gear_N_maps_to_zero()
            => Assert.Equal(0.0, PropertyCoercion.Coerce("N", "gear"));

        [Fact]
        public void Numeric_string_parses()
            => Assert.Equal(3.0, PropertyCoercion.Coerce("3", "gear"));

        [Fact]
        public void Null_returns_zero()
            => Assert.Equal(0.0, PropertyCoercion.Coerce(null, "nullprop"));

        [Fact]
        public void Unsupported_object_returns_zero()
            => Assert.Equal(0.0, PropertyCoercion.Coerce(new object(), "weird"));

        [Fact]
        public void ParseGear_matches_legacy_behaviour()
        {
            Assert.Equal(-1.0, PropertyCoercion.ParseGear("R"));
            Assert.Equal(0.0, PropertyCoercion.ParseGear("N"));
            Assert.Equal(3.0, PropertyCoercion.ParseGear("3"));
            Assert.Equal(0.0, PropertyCoercion.ParseGear(null));
            Assert.Equal(0.0, PropertyCoercion.ParseGear(""));
            Assert.Equal(0.0, PropertyCoercion.ParseGear("garbage"));
        }
    }
}
