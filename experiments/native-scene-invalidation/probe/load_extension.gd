extends SceneTree

func _initialize() -> void:
	var descriptor := OS.get_environment("SPI_NATIVE_OBSERVER_DESCRIPTOR")
	var status := GDExtensionManager.load_extension(descriptor)
	if status != GDExtensionManager.LOAD_STATUS_OK:
		push_error("absolute-path GDExtension load failed: %s" % status)
		quit(1)
		return
	quit(0)
