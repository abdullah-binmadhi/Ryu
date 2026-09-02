import Foundation
import Metal
import MetalFX

if let cls = NSClassFromString("MTL4CommandQueue") {
    print("MTL4CommandQueue EXISTS")
} else {
    print("MTL4CommandQueue DOES NOT EXIST")
}

if let cls = NSClassFromString("MTLFXSpatialScalerDescriptor") {
    print("MTLFXSpatialScalerDescriptor EXISTS")
} else {
    print("MTLFXSpatialScalerDescriptor DOES NOT EXIST")
}
