import os
import tempfile
import unittest
from unittest import mock

try:
    import tkinter
except ImportError:
    gui = None
else:
    import gui


@unittest.skipIf(gui is None, 'Tkinter недоступен')
class ReviewGuiTest(unittest.TestCase):
    def test_broken_config_preserved_and_password_denied(self):
        with tempfile.TemporaryDirectory() as folder:
            path = os.path.join(folder, 'config.json')
            with open(path, 'w') as stream:
                stream.write('{broken')
            with mock.patch.object(gui, 'CONFIG_PATH', path):
                self.assertIsNone(gui._get_exit_password())
                app = gui.App.__new__(gui.App)
                app.pw_status = mock.Mock()
                app._refresh_pw_status()
                self.assertIn('настро', app.pw_status.set.call_args.args[0].lower())
            with open(path) as stream:
                self.assertEqual(stream.read(), '{broken')

    def test_safe_removal_uses_engine_status_with_empty_tiles(self):
        app = gui.App.__new__(gui.App)
        app.workers_data = {}
        app.mon_status = mock.Mock()
        with mock.patch.object(gui, 'safe_removal_active', return_value=True), mock.patch.object(gui, 'safe_removal_status', create=True, return_value=('blocked', 'Размонтирование не завершено')):
            app._update_safe_removal_status()
        app.mon_status.set.assert_called_once_with('Размонтирование не завершено')

    def test_search_walks_each_directory_once_and_cancels_stale(self):
        app = gui.App.__new__(gui.App)
        app.root = mock.Mock()
        app._search_gen = 1
        connection = mock.Mock()
        connection.execute.return_value.fetchall.return_value = [(1, '', '', '/archive')] * 100
        app._get_db = mock.Mock(return_value=connection)
        params = dict(dt_from=None, dt_to=None, dev_id=None, person='', filetype='', filename='absent')
        with mock.patch.object(gui.os.path, 'isdir', return_value=True), mock.patch.object(gui.os, 'walk', return_value=[]) as walk:
            app._search_worker(params, 1)
            self.assertEqual(walk.call_count, 1)
            walk.reset_mock()
            app._search_worker(params, 0)
            walk.assert_not_called()

    def test_reset_error_survives_deferred_callback(self):
        for error in (OSError('disk unavailable'), gui.sqlite3.DatabaseError('database disk image is malformed')):
            with self.subTest(error=type(error).__name__):
                app = gui.App.__new__(gui.App)
                app.root = mock.Mock()
                app.workers_data = {}
                app._reset_status_var = mock.Mock()
                app._finish_factory_reset = mock.Mock()
                with mock.patch.object(gui.messagebox, 'askyesno', return_value=True), mock.patch.object(gui, 'factory_reset', side_effect=error), mock.patch.object(gui.threading, 'Thread') as thread:
                    app._confirm_factory_reset()
                    thread.call_args.kwargs['target']()
                _, callback, *args = app.root.after.call_args.args
                callback(*args)
                app._finish_factory_reset.assert_called_once_with(None, str(error))

    def test_failed_setting_save_preserves_runtime_value(self):
        app = gui.App.__new__(gui.App)
        app.root = mock.Mock()
        app._timeout_var = mock.Mock()
        app._timeout_var.get.return_value = '5'
        app._lock_timeout = 600
        app._refresh_timeout_status = mock.Mock()
        with mock.patch.object(gui, '_update_config', return_value=False), mock.patch.object(gui.messagebox, 'showerror') as error:
            app._save_lock_timeout()
        self.assertEqual(app._lock_timeout, 600)
        error.assert_called_once()

    def test_cleanup_lock_error_is_displayed(self):
        app = gui.App.__new__(gui.App)
        app.root = mock.Mock()
        app._cleanup_days_var = mock.Mock()
        app._cleanup_days_var.get.return_value = '30'
        app._cleanup_status_var = mock.Mock()
        with mock.patch.object(gui, 'cleanup_old_backup_videos', side_effect=OSError('busy')), mock.patch.object(gui.threading, 'Thread') as thread:
            app._run_cleanup_now()
            thread.call_args.kwargs['target'](*thread.call_args.kwargs.get('args', ()))
        _, callback, *args = app.root.after.call_args.args
        callback(*args)
        self.assertIn('busy', app._cleanup_status_var.set.call_args.args[0])

    def test_search_cancels_during_directory_walk(self):
        app = gui.App.__new__(gui.App)
        app.root = mock.Mock()
        app._search_gen = 1
        connection = mock.Mock()
        connection.execute.return_value.fetchall.return_value = [(1, '', '', '/archive')]
        app._get_db = mock.Mock(return_value=connection)
        params = dict(dt_from=None, dt_to=None, dev_id=None, person='', filetype='', filename='absent')
        visited = []
        def walk(_):
            yield '/archive', [], []
            app._search_gen = 2
            yield '/archive/next', [], []
            visited.append('stale work')
        with mock.patch.object(gui.os.path, 'isdir', return_value=True), mock.patch.object(gui.os, 'walk', side_effect=walk):
            app._search_worker(params, 1)
        self.assertEqual(visited, [])
        app.root.after.assert_not_called()

    def test_settings_tab_builds_with_broken_config(self):
        with tempfile.TemporaryDirectory() as folder:
            path = os.path.join(folder, 'config.json')
            with open(path, 'w') as stream:
                stream.write('{broken')
            app = gui.App.__new__(gui.App)
            app.C = {'brand': 'blue', 'fg_muted': 'gray', 'bg_app': 'black'}
            app._bay_count = 10
            app._lock_timeout = 600
            app._cleanup_enabled = False
            app._cleanup_days = 30
            with mock.patch.object(gui, 'CONFIG_PATH', path), mock.patch.object(gui.usb_monitor, '_CONFIG_PATH', path), mock.patch.object(gui, 'tk') as tk_mock, mock.patch.object(gui, 'ttk'):
                tk_mock.StringVar.side_effect = lambda **kwargs: mock.Mock()
                app._build_settings_tab(mock.Mock())
                app.pw_status.set.assert_called_with(gui.CONFIG_ERROR)
            with open(path) as stream:
                self.assertEqual(stream.read(), '{broken')

    def test_manual_device_cleanup_cannot_remove_copy_in_progress(self):
        with tempfile.TemporaryDirectory() as folder:
            device = os.path.join(folder, 'Device123')
            os.mkdir(device)
            video = os.path.join(device, 'record.mp4')
            with open(video, 'wb') as stream:
                stream.write(b'only backup')
            app = gui.App.__new__(gui.App)
            app.root = mock.Mock()
            app.edit_dev_id = mock.Mock()
            app.edit_dev_id.get.return_value = '123'
            app._device_label = mock.Mock(return_value='123')
            with mock.patch.object(gui, 'get_dest_base', return_value=folder), mock.patch.object(gui.usb_monitor, 'DB_PATH', os.path.join(folder, 'devices.db')), mock.patch.object(gui, 'messagebox') as dialogs:
                dialogs.askyesno.return_value = True
                with gui.usb_monitor.operation_guard():
                    app._clean_device_videos()
            self.assertTrue(os.path.isfile(video), 'Очистка уничтожила копию занятого архива')
