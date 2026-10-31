package app.linc.android.service

import app.linc.android.service.BankingPause.Action
import org.junit.Assert.assertEquals
import org.junit.Test

class BankingPauseTest {
    private val bank = "com.example.bank"
    private val pay = "com.example.pay"
    private val chosen = setOf(bank, pay)

    @Test fun opening_a_picked_app_pauses() = assertEquals(Action.Pause, BankingPause.decide(bank, chosen, paused = false))

    @Test fun staying_in_it_does_nothing() = assertEquals(Action.None, BankingPause.decide(bank, chosen, paused = true))

    @Test fun leaving_for_another_app_restores() = assertEquals(Action.Restore, BankingPause.decide("com.android.chrome", chosen, paused = true))

    @Test fun moving_between_picked_apps_stays_paused() = assertEquals(Action.None, BankingPause.decide(pay, chosen, paused = true))

    @Test fun no_recent_switch_keeps_things_as_they_are() {
        assertEquals(Action.None, BankingPause.decide(null, chosen, paused = true))
        assertEquals(Action.None, BankingPause.decide(null, chosen, paused = false))
    }

    @Test fun emptying_the_list_while_paused_restores() = assertEquals(Action.Restore, BankingPause.decide(bank, emptySet(), paused = true))

    @Test fun other_apps_never_pause() = assertEquals(Action.None, BankingPause.decide("com.android.chrome", chosen, paused = false))
}
