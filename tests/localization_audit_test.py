"""Guard against silently skipped languages and installer checks in CI."""
import sys
import unittest
from pathlib import Path
from tempfile import TemporaryDirectory
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'scripts'))
import audit_localization as audit


class LocalizationAuditTests(unittest.TestCase):
    def catalog(self):
        return {language: {'Greeting': 'Hello {0}'} for language in audit.LANGUAGES}

    def test_extra_language_placeholders_are_checked(self):
        catalog = self.catalog()
        catalog['fr-FR'] = {'Greeting': 'Bonjour {1}'}
        errors = []
        audit.check_catalog('Fixture', catalog, errors)
        self.assertIn('Fixture/Greeting: placeholder mismatch', errors)

    def test_extra_language_missing_and_empty_values_are_checked(self):
        for value, expected in [({}, 'missing fr-FR'), ({'Greeting': ''}, 'empty fr-FR')]:
            with self.subTest(value=value):
                catalog = self.catalog()
                catalog['fr-FR'] = value
                errors = []
                audit.check_catalog('Fixture', catalog, errors)
                self.assertTrue(any(expected in message for message in errors), errors)

    def test_missing_language_reports_error_instead_of_crashing(self):
        catalog = self.catalog()
        del catalog['zh-TW']
        errors = []
        audit.check_catalog('Fixture', catalog, errors)
        self.assertIn('Fixture: missing language zh-TW', errors)

    def test_all_empty_dictionaries_fail(self):
        errors = []
        audit.check_catalog('Fixture', {language: {} for language in audit.LANGUAGES}, errors)
        self.assertTrue(errors, 'Empty dictionaries must not pass the audit')

    def test_ci_requires_installer_but_local_audit_can_warn(self):
        message = 'Installer language resources unavailable'
        with patch.object(audit, 'installer_resources', return_value=({}, message)):
            for strict in (False, True):
                with self.subTest(strict=strict):
                    *_, errors, warnings = audit.audit(require_installer=strict)
                    self.assertEqual(message in errors, strict)
                    self.assertEqual(message in warnings, not strict)


class InstallerLocalizationTests(unittest.TestCase):
    def setUp(self):
        self.workspace = TemporaryDirectory(prefix='iphoneMirror-installer-audit-')
        self.addCleanup(self.workspace.cleanup)
        self.root = Path(self.workspace.name)
        self.compiler = self.root / 'work/tools/inno-setup'
        self.installer = self.root / 'installer/iPhoneMirror.iss'
        self.installer.parent.mkdir(parents=True)
        self.root_patch = patch.object(audit, 'ROOT', self.root)
        self.root_patch.start()
        self.addCleanup(self.root_patch.stop)
        self.declarations = []
        for code, relative, language_id in [
            ('chinesesimp', 'compiler:Languages/ChineseSimplified.isl', '$0804'),
            ('chinesetrad', 'compiler:Languages/ChineseTraditional.isl', '$0C04'),
            ('chinesetaiwan', 'Languages/ChineseTraditionalTaiwan.isl', '$0404'),
            ('english', 'compiler:Default.isl', '$0409'),
        ]:
            path = (self.compiler / relative.removeprefix('compiler:') if relative.startswith('compiler:')
                    else self.installer.parent / relative)
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(f'[LangOptions]\nLanguageID={language_id}\n[Messages]\nHello=Hello %1\n', encoding='utf-8')
            self.declarations.append(f'Name: "{code}"; MessagesFile: "{relative}"')
        self.write_script()

    def write_script(self, suffix=''):
        self.installer.write_text('[Languages]\n' + '\n'.join(self.declarations) + '\n' + suffix, encoding='utf-8')

    def test_removed_installer_language_does_not_pass_using_cached_file(self):
        self.declarations = [line for line in self.declarations if 'chinesetaiwan' not in line]
        self.write_script()
        with self.assertRaisesRegex(ValueError, 'missing.*chinesetaiwan'):
            audit.installer_resources()

    def test_actual_declared_messages_file_is_used(self):
        self.declarations[-1] = 'Name: "english"; MessagesFile: "compiler:Missing.isl"'
        self.write_script()
        catalog, warning = audit.installer_resources()
        self.assertFalse(catalog)
        self.assertIsNotNone(warning)

    def test_unqualified_messages_apply_to_all_languages(self):
        self.write_script('[CustomMessages]\nShared=Shared %1\nenglish.Shared=English %1\n')
        catalog, warning = audit.installer_resources()
        self.assertIsNone(warning)
        self.assertEqual(catalog['en-US']['CustomMessages/Shared'], 'English %1')
        self.assertEqual(catalog['zh-TW']['CustomMessages/Shared'], 'Shared %1')

    def test_wrong_language_file_is_rejected(self):
        self.declarations[0] = 'Name: "chinesesimp"; MessagesFile: "compiler:Default.isl"'
        self.write_script()
        with self.assertRaisesRegex(ValueError, 'LanguageID'):
            audit.installer_resources()


if __name__ == '__main__':
    unittest.main()
