-include ../config.mk

GAME            ?= ra

LSCRIPT         ?= patch.lds
INBIN           ?= bin.dat
PHYS_ALIGNMENT  ?= 0x400
VIRT_ALIGNMENT  ?= 0x1000

IMPORT          ?= 1 0x1E6000 6167

INCLUDES        ?= -Iinc/ -I../shared/inc/

LIBS            ?=
OBJS            ?= \
    src/unique_hotkeys.o \
	src/unique_hotkeys-func.o \
	src/gethostbyname_crash_fix.o \
	src/threading_crash_fix.o \
	src/destroy_window.o \
	src/auto_ally.o \
	src/read_scenario_ini.o \
	src/read_scenario_ini-func.o \
	src/more_colour_remaps.o \
	src/frameskip.o \
	src/hires-func.o \
	src/debug-func.o \
	src/infantryclass__can_enter_cell_crash_fix.o \
	src/cellclass__cell_color_crash_fix.o \
	src/terrainclass_radar_icon_crash_fix.o \
	src/triggerclass_spring_crash_fix.o \
	src/mod_map_combat_anim_crash_fix.o \
	src/overlay_structures_crash_fix.o \
	src/displayclass__refresh_cells_crash_fix.o \
	src/hide_names.o \
	src/queue_exit.o \
	src/print_dolist.o \
	src/print_dolist_func.o \
	src/replay.o \
	src/replay-native.o \
	src/replay-native-hooks.o \
	src/vendor/miniz/miniz.o \
	src/auto-resign-func.o \
	src/buildingtypeclass_width_crash_fix.o \
	src/out_of_sync-func.o \
	src/cell_shadow_crash_fix.o \
	src/chat-func.o \
	src/chat.o \
	src/houseclass.o \
	src/quick_save.o \
	src/event_exit.o \
	src/out_of_sync.o \
	src/themeclass_play_song_rate_limit.o \
	src/mouse_fixes.o \
	src/cache_important_mix_files.o \
	src/max_units_bug_func.o \
	src/chatallies.o \
	src/debug.o \
	src/chat_message_buffer_fix.o \
	src/cryptography.o \
	src/crc32.o \
	src/Sha256.o \
	src/nethack.o \
	src/netkey.o \
	src/limit_cpu_usage.o \
	src/strip_cameos_glitch_bug.o \
	src/super_tesla_fix.o \
	src/scrollrate_fix.o \
	src/waiting_for_players.o \
	src/waiting_for_players-func.o \
	src/spectators-func.o \
	src/cpu_affinity_freeze_crash.o \
	src/music_loading.o \
	src/auto_snapshot.o \
	src/asctime.o \
	src/map_snapshot.o \
	src/always_show_cursor.o \
	src/video_stretching_helpers.o \
	src/max_units_bug.o \
	src/image_keyword_fix.o \
	src/localise_draw_strings.o \
	src/movie_loading.o \
	src/auto-resign.o \
	src/short_game.o \
	src/engi_q_freeze_fix.o \
	src/extra_theaters.o \
	src/fence_bug.o \
	src/improved_error_messages.o \
	src/nocd.o \
	src/spawn.o \
	src/savegame_bug.o \
	src/loading.o \
	src/extended_sidebar.o \
	src/hires.o \
	src/vqa-scale.o \
	src/ext/savegame_support.o \
	src/ext/extended_houseclass.o \
	src/mcvundeploy.o \
	src/animate_score_objects_crash_fix.o \
	src/spectator.o \
	src/fix_multiplayer_settings_saving.o \
	src/always_load_building_icons.o \
	src/harvester_harvest_closest_ore.o \
	src/ai_fixes.o \
	src/hotkeys.o \
	src/spawner_stats.o \
	src/optional_scorescreen.o \
	src/skirmish_savegames.o \
	src/expansions.o \
	src/exception.o \
	src/tags_bug.o \
	src/ingame_chat_improvments.o \
	src/ingame_display_messages_from_yourself.o \
	src/game_difficulty_speed_modifier_remove.o \
	src/selectable_spawn_locations.o \
	src/predetermined_alliances.o \
	src/Loop_Over_RULES_INI_Section_Entries_.o \
	src/radar_dome_crash_fix.o \
	src/what_weapon_should_i_use_crash_fix.o \
	src/zoom_out_radar_by_default.o \
	src/no_digest.o \
	src/mousewheel_scrolling.o \
	src/lock_gamespeed.o \
	src/fix_savegame_resolution_sidebar.o \
	src/spawner_files.o \
	src/load_more_mix_files.o \
	src/unit_selection_click_bug.o \
	src/skip_deleting_conquer_eng.o \
	src/spawner_house_colours_countries_handicaps.o \
	src/shorter_multiplayer_reconnect_timer.o \
	src/coop.o \
	src/radar_spectator.o \
	src/fix_formation_glitch.o \
	src/parabombs_multiplayer.o \
	src/ally_shroud_reveal.o \
	src/forced_alliances.o \
	src/build_off_ally.o \
	src/no_screenshake.o \
	src/magic_build_fix.o \
	src/no_tesla_zap_effect_delay.o \
	src/infantry_range_check.o \
	src/aftermath_fast_buildspeed_option.o \
	src/sidebar_special_houses.o \
	src/auto_harvest.o \
	src/no_vortex_files.o \
	\
	watcall.o \
	sym.o \
	res/res.o

#        imports.o \


DLL_OBJS        ?= bin.o # now this is a hack

.PHONY: default
default: $(GAME).exe

$(GAME).exe: .dump-.patch-.import-.$(GAME).exe
	$(CP) $< $@

pure-$(GAME).exe: .dump-.import-.pure-$(GAME).exe
	$(CP) $< $@

include ./generic.mk

#CFLAGS          += -mpush-args -mno-accumulate-outgoing-args -mno-stack-arg-probe -O0
CFLAGS          += -mno-sse -mno-sse2
WFLAGS          += -Ires/ -I../shared/res/

# These modules use private cdecl memory helpers, not Watcom's game CRT.
src/replay-native.o src/vendor/miniz/miniz.o: CFLAGS += -ffreestanding -fno-builtin -fno-strict-aliasing -fno-stack-protector -fno-asynchronous-unwind-tables -mno-stack-arg-probe -DNDEBUG -DMINIZ_NO_STDIO -DMINIZ_NO_ARCHIVE_APIS -DMINIZ_NO_ZLIB_APIS -DMINIZ_NO_MALLOC -Dmemcpy=nr_memcpy -Dmemset=nr_memset -Dmemmove=nr_memmove -Dmemcmp=nr_memcmp -Dstrlen=nr_strlen -include src/replay-native.h

ifdef WWDEBUG
    CFLAGS += -D WWDEBUG
    NFLAGS += -D WWDEBUG
endif
